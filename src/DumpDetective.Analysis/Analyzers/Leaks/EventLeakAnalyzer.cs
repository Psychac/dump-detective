using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Detects long-lived event-publisher/subscriber chains ("event leaks") — a publisher whose
/// delegate field keeps subscribers alive well past their expected lifetime.
/// </summary>
/// <remarks>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — the
/// largest analyzer in this wave (~3,000 lines pre-retyping). Its scan (module/type discovery,
/// delegate-chain walking, subscriber type/method resolution, root-hint mapping, disposal
/// checking) is deliberately, measurably perf-sensitive dump-side work — ported unchanged into
/// <see cref="EventLeakAnalyzerLegacyAdapter"/> rather than retargeted onto Tier-2 field-read
/// primitives (see that type's own remarks for why). This analyzer receives the adapter's
/// finished per-group aggregates via <see cref="EventLeakAnalyzerOptions"/> and does only the
/// capability-light tail: significance filtering, severity-ranked snapshot assembly, retained-bytes
/// estimation, and root-path evidence enrichment — the same "adapter accumulates real data,
/// hands its inner analyzer the finished result" shape <c>HangAnalyzer</c>'s own retyping
/// established, applied at a larger scale here since more of this analyzer's own logic (not just
/// field reads) is genuinely dump-side-shaped.
///
/// Retargets: <c>BuildTypeSizeMap</c> onto <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/>
/// (replacing <c>IHeapAnalysisCache.GetOrBuildTypeStatistics</c> directly — same average-size-per-type
/// fold); the per-subscriber exact-retained-bytes backfill onto
/// <see cref="IHeapDominatorQuery.TryGetRetainedSize"/> (already existed — every prior
/// <see cref="IHeapDominatorQuery"/> consumer already used this same member, nothing new here);
/// and <c>PopulateEvidence</c>'s root-path search onto <see cref="IHeapRootPathQuery.TryFindRootPath"/>
/// (replacing <c>RootPathFinder</c>/<c>ReferenceGraph</c>/<c>cache.GetOrBuildValidRoots</c> directly
/// — the same retarget every prior batch's evidence-path search made). Requires zero brand-new
/// Tier-2 primitives.
/// </remarks>
public sealed class EventLeakAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Event Leak Analysis";
    public string Category => "Events";

    public AnalyzerDomainResult? LastResult { get; private set; }

    // Cap on the correlation views (design §7) — first-class cross-group folds, not a
    // per-group re-derivation.
    private const int TopCorrelationEntries = 20;

    // Schema for EventLeakEvidence — bump when the shape or its population rules change.
    private const int EventLeakEvidenceSchemaVersion = 1;

    private static readonly RootPathSearchOptions RootPathOptions = new(
        MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");
        IHeapDominatorQuery? dominators = context.HeapDominators;

        var analyzerOptions = context.AnalyzerOptions as EventLeakAnalyzerOptions
            ?? throw new InvalidOperationException($"{Name} requires an {nameof(EventLeakAnalyzerOptions)} payload from its adapter.");

        LastResult = Analyze(typeStatistics, dominators, rootPaths, analyzerOptions, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static EventLeakDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics, IHeapDominatorQuery? dominators, IHeapRootPathQuery rootPaths,
        EventLeakAnalyzerOptions analyzerOptions, CancellationToken cancellationToken)
    {
        EventLeakOptions options = analyzerOptions.Options;
        List<EventGroupInfo> groupedLeaks = analyzerOptions.GroupedLeaks;
        int eventsScanned = analyzerOptions.EventsScanned;
        int publisherInstances = analyzerOptions.PublisherInstances;
        int candidatePublisherTypeCount = analyzerOptions.CandidatePublisherTypeCount;
        int leakingPublisherTypeCount = analyzerOptions.LeakingPublisherTypeCount;

        // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): exact clean-vs-leaking
        // split, MT-keyed on both sides (registry candidates and leakingMTs), so it stays
        // correct even where a name-based fold would double-count or undercount types that
        // share a display name.
        int cleanPublisherTypeCount = Math.Max(0, candidatePublisherTypeCount - leakingPublisherTypeCount);

        if (groupedLeaks.Count == 0)
        {
            return new EventLeakDomainResult(0, 0, 0, 0,
                TotalEventsScanned: eventsScanned,
                TotalPublisherInstances: publisherInstances,
                PublisherTypesScanned: candidatePublisherTypeCount,
                CleanPublisherTypeCount: cleanPublisherTypeCount);
        }

        // Build type-size map once for EstimatedSubscriberRetainedBytes computation.
        Dictionary<string, ulong> typeSizeMap = BuildTypeSizeMap(typeStatistics);

        // Back-fill SubscriberSize on stored instances now that typeSizeMap is available.
        // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): prefer the
        // exact dominator tree's per-subscriber retained bytes over the per-type shallow-size
        // average — the addresses here are real (only the capped Instances list, not the
        // AllSubscriberTypeCounts fold used by EstimateGroupRetainedBytes, which has no
        // addresses to look up).
        for (int i = 0; i < groupedLeaks.Count; i++)
            foreach (EventLeakInfo inst in groupedLeaks[i].Instances)
                foreach (SubscriberInfo s in inst.Subscribers)
                {
                    if (s.SubscriberSize != 0)
                        continue;

                    if (dominators is not null && dominators.TryGetRetainedSize(s.Address, out ulong exactBytes))
                    {
                        s.SubscriberSize = exactBytes;
                        s.SubscriberSizeIsExact = true;
                    }
                    else if (typeSizeMap.TryGetValue(s.Type, out ulong sz))
                    {
                        s.SubscriberSize = sz;
                    }
                }

        int totalSubscribers = 0;
        int staticLeaks = 0;
        int instanceLeaks = 0;
        for (int i = 0; i < groupedLeaks.Count; i++)
        {
            totalSubscribers += groupedLeaks[i].TotalSubscribers;
            if (groupedLeaks[i].IsStatic) staticLeaks++; else instanceLeaks++;
        }

        var topPublisherEvents = new List<NameCountEntry>(Math.Min(10, groupedLeaks.Count));
        // Manual top-50 by TotalSubscribers (avoid LINQ on large lists)
        var sortedGroups = new List<EventGroupInfo>(groupedLeaks);
        sortedGroups.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));
        int topN = Math.Min(50, sortedGroups.Count);
        for (int i = 0; i < topN; i++)
            topPublisherEvents.Add(new NameCountEntry($"{sortedGroups[i].PublisherType}.{sortedGroups[i].EventFieldName}", sortedGroups[i].TotalSubscribers));

        // Richer summary rows for the report table (separate class/event, add instance count + retained size).
        var topPublisherEventsFull = new List<PublisherEventSummary>(topN);
        for (int i = 0; i < topN; i++)
        {
            var g = sortedGroups[i];
            topPublisherEventsFull.Add(new PublisherEventSummary(
                g.PublisherType,
                g.EventFieldName,
                g.TotalSubscribers,
                g.InstanceCount,
                EstimateGroupRetainedBytes(g, typeSizeMap)));
        }

        var topLeakGroups = new List<EventLeakGroupSnapshot>(groupedLeaks.Count);
        for (int i = 0; i < groupedLeaks.Count; i++)
        {
            var g = groupedLeaks[i];
            // Use pre-aggregated subscriber type counts from ALL instances.
            // g.Instances is capped at TopDetailedInstancesPerGroup — re-deriving
            // subTypeCounts from it would only reflect those few stored instances.
            var subTypeCounts = g.AllSubscriberTypeCounts
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
            bool groupHasDuplicates = false;
            int groupDisposedButSubscribedInstances = 0;
            bool groupHasLifetimeMismatch = false;
            for (int j = 0; j < g.Instances.Count; j++)
            {
                var gInst = g.Instances[j];
                if (gInst.DuplicateSubscriptionCount > 0) groupHasDuplicates = true;
                if (gInst.IsDisposedButSubscribed) groupDisposedButSubscribedInstances++;
                if (gInst.HasLifetimeMismatch) groupHasLifetimeMismatch = true;
            }
            var topSubTypes = new List<NameCountEntry>(subTypeCounts.Count);
            foreach (var kvp in subTypeCounts.OrderByDescending(kv => kv.Value))
                topSubTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));

            topLeakGroups.Add(new EventLeakGroupSnapshot(
                g.PublisherType,
                g.EventFieldName,
                g.IsStatic,
                g.SeverityScore,
                g.InstanceCount,
                g.TotalSubscribers,
                g.AverageSubscribers,
                g.MinSubscribers,
                g.MaxSubscribers,
                topSubTypes,
                EstimateGroupRetainedBytes(g, typeSizeMap),
                HasDuplicateSubscriptions: groupHasDuplicates,
                DisposedButSubscribedInstances: groupDisposedButSubscribedInstances,
                HasLifetimeMismatch: groupHasLifetimeMismatch,
                IsTimerEvent: IsTimerEvent(g.PublisherType, g.EventFieldName),
                IsPropertyChangedEvent: IsPropertyChangedEvent(g.EventFieldName)));
        }

        int timerEventLeakGroupCount = 0;
        int propertyChangedEventLeakGroupCount = 0;
        for (int i = 0; i < topLeakGroups.Count; i++)
        {
            if (topLeakGroups[i].IsTimerEvent) timerEventLeakGroupCount++;
            if (topLeakGroups[i].IsPropertyChangedEvent) propertyChangedEventLeakGroupCount++;
        }

        var topLeakInstances = new List<EventLeakInstanceSnapshot>();
        for (int i = 0; i < groupedLeaks.Count; i++)
        {
            var g = groupedLeaks[i];
            for (int j = 0; j < g.Instances.Count; j++)
            {
                var inst = g.Instances[j];
                var subTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (SubscriberInfo s in inst.Subscribers)
                {
                    subTypeCounts.TryGetValue(s.Type, out int cnt);
                    subTypeCounts[s.Type] = cnt + 1;
                }
                var subTypeList = new List<SubscriberTypeCount>(subTypeCounts.Count);
                foreach (var kvp in subTypeCounts)
                    subTypeList.Add(new SubscriberTypeCount(kvp.Key, kvp.Value));
                subTypeList.Sort((a, b) => b.Count.CompareTo(a.Count));

                // Build per-subscriber detail rows (deduplicated by type+method, summed count).
                // An exact retained-bytes value (§9) always wins as the representative size
                // over the per-type shallow-size average, even if a same-key entry was seen first.
                var detailKey = new Dictionary<(string Type, string? Method), (int Count, ulong Size, bool SizeIsExact)>(inst.Subscribers.Count);
                foreach (SubscriberInfo s in inst.Subscribers)
                {
                    var key = (s.Type, s.MethodName);
                    detailKey.TryGetValue(key, out var existing);
                    bool preferNew = s.SubscriberSizeIsExact && !existing.SizeIsExact;
                    ulong size = preferNew || (existing.Size == 0 && s.SubscriberSize > 0) ? s.SubscriberSize : existing.Size;
                    bool isExact = existing.SizeIsExact || s.SubscriberSizeIsExact;
                    detailKey[key] = (existing.Count + 1, size, isExact);
                }
                var subDetails = new List<SubscriberDetail>(detailKey.Count);
                foreach (var kvp in detailKey.OrderByDescending(kv => kv.Value.Count))
                    subDetails.Add(new SubscriberDetail(kvp.Key.Type, kvp.Key.Method, kvp.Value.Size, kvp.Value.Count, kvp.Value.SizeIsExact));

                topLeakInstances.Add(new EventLeakInstanceSnapshot(
                    g.PublisherType,
                    g.EventFieldName,
                    g.IsStatic,
                    inst.PublisherAddress,
                    inst.SeverityScore,
                    inst.SubscriberCount,
                    string.IsNullOrWhiteSpace(inst.RootHint) ? null : inst.RootHint,
                    subTypeList,
                    PublisherGeneration: inst.PublisherGeneration,
                    DuplicateSubscriptionCount: inst.DuplicateSubscriptionCount,
                    IsDisposedButSubscribed: inst.IsDisposedButSubscribed,
                    HasLifetimeMismatch: inst.HasLifetimeMismatch,
                    SubscriberDetails: subDetails));
            }
        }
        topLeakInstances.Sort((a, b) =>
        {
            int cmp = b.SeverityScore.CompareTo(a.SeverityScore);
            return cmp != 0 ? cmp : b.SubscriberCount.CompareTo(a.SubscriberCount);
        });

        ulong totalEstimatedRetainedBytes = 0;
        for (int i = 0; i < topLeakGroups.Count; i++)
            totalEstimatedRetainedBytes += topLeakGroups[i].EstimatedSubscriberRetainedBytes;

        // Every instance is enrichment-eligible now (§9.19) — topLeakInstances is already
        // sorted by severity descending, so MaxEvidenceEnrichmentMs's wall-clock budget alone
        // governs how much of the priority-ordered work actually completes.
        PopulateEvidence(rootPaths, topLeakInstances, options, cancellationToken);

        var topSubscriberTypesAcrossGroups = BuildTopSubscriberTypesAcrossGroups(groupedLeaks, TopCorrelationEntries);
        var topHandlerMethodsAcrossGroups = BuildTopHandlerMethodsAcrossGroups(groupedLeaks, TopCorrelationEntries);
        var subscriberCountHistogram = BuildSubscriberCountHistogram(groupedLeaks);

        return new EventLeakDomainResult(
            groupedLeaks.Count,
            totalSubscribers,
            staticLeaks,
            instanceLeaks,
            topPublisherEvents,
            topLeakGroups,
            topLeakInstances,
            TotalEventsScanned: eventsScanned,
            TotalPublisherInstances: publisherInstances,
            TopPublisherEvents: topPublisherEventsFull,
            TotalEstimatedRetainedBytes: totalEstimatedRetainedBytes,
            TopSubscriberTypesAcrossGroups: topSubscriberTypesAcrossGroups,
            TopHandlerMethodsAcrossGroups: topHandlerMethodsAcrossGroups,
            PublisherTypesScanned: candidatePublisherTypeCount,
            CleanPublisherTypeCount: cleanPublisherTypeCount,
            TimerEventLeakGroupCount: timerEventLeakGroupCount,
            PropertyChangedEventLeakGroupCount: propertyChangedEventLeakGroupCount,
            SubscriberCountHistogram: subscriberCountHistogram);
    }

    /// <summary>
    /// Phase E (design §7): folds each group's <see cref="EventGroupInfo.AllSubscriberTypeCounts"/>
    /// into one cross-group ranking. Pure in-memory fold over already-computed per-group
    /// dictionaries — no heap access, no ClrMD. Surfaces "one type subscribing to many
    /// different publishers," which no per-group view can show.
    /// </summary>
    internal static List<NameCountEntry> BuildTopSubscriberTypesAcrossGroups(List<EventGroupInfo> groups, int topN)
    {
        var folded = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++)
        {
            var counts = groups[i].AllSubscriberTypeCounts;
            if (counts is null)
                continue;

            foreach (var kvp in counts)
            {
                folded.TryGetValue(kvp.Key, out int existing);
                folded[kvp.Key] = existing + kvp.Value;
            }
        }
        return TopNByCount(folded, topN, static (type, count) => new NameCountEntry(type, count));
    }

    /// <summary>
    /// Phase E (design §7): folds each group's <see cref="EventGroupInfo.AllSubscriberMethodCounts"/>
    /// (keyed by subscriber type + handler method) into one cross-group ranking. Identifies a
    /// single factory or wiring method responsible for bulk subscription registration.
    /// </summary>
    internal static List<NameCountEntry> BuildTopHandlerMethodsAcrossGroups(List<EventGroupInfo> groups, int topN)
    {
        var folded = new Dictionary<(string Type, string? MethodName), int>();
        for (int i = 0; i < groups.Count; i++)
        {
            var counts = groups[i].AllSubscriberMethodCounts;
            if (counts is null)
                continue;

            foreach (var kvp in counts)
            {
                folded.TryGetValue(kvp.Key, out int existing);
                folded[kvp.Key] = existing + kvp.Value;
            }
        }

        var named = new Dictionary<string, int>(folded.Count, StringComparer.Ordinal);
        foreach (var kvp in folded)
        {
            string name = $"{kvp.Key.Type}.{kvp.Key.MethodName ?? "?"}";
            named.TryGetValue(name, out int existing);
            named[name] = existing + kvp.Value;
        }
        return TopNByCount(named, topN, static (name, count) => new NameCountEntry(name, count));
    }

    /// <summary>
    /// P3-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): folds each group's
    /// <see cref="EventGroupInfo.SubscriberCountBuckets"/> — itself already accumulated across
    /// ALL instances in the group, not just the capped <c>TopInstances</c> — into one
    /// cross-group subscriber-count distribution. Pure in-memory fold, no heap access.
    /// Bucket order is preserved (ascending by subscriber count) rather than sorted by count,
    /// since a histogram reads correctly only in its natural order.
    /// </summary>
    internal static List<NameCountEntry> BuildSubscriberCountHistogram(List<EventGroupInfo> groups)
    {
        var totals = new int[EventLeakAnalyzerLegacyAdapter.SubscriberCountHistogramBuckets.Length];
        for (int i = 0; i < groups.Count; i++)
        {
            int[]? buckets = groups[i].SubscriberCountBuckets;
            if (buckets is null)
                continue;

            for (int b = 0; b < totals.Length; b++)
                totals[b] += buckets[b];
        }

        var result = new List<NameCountEntry>(totals.Length);
        for (int b = 0; b < totals.Length; b++)
            result.Add(new NameCountEntry(EventLeakAnalyzerLegacyAdapter.SubscriberCountHistogramBuckets[b].Label, totals[b]));
        return result;
    }

    private static List<NameCountEntry> TopNByCount(Dictionary<string, int> counts, int topN, Func<string, int, NameCountEntry> makeEntry)
    {
        var entries = new List<NameCountEntry>(counts.Count);
        foreach (var kvp in counts)
            entries.Add(makeEntry(kvp.Key, kvp.Value));

        entries.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (entries.Count > topN)
            entries.RemoveRange(topN, entries.Count - topN);
        return entries;
    }

    private static List<EvidenceSignal> BuildInstanceSignals(EventLeakInstanceSnapshot inst)
    {
        var signals = new List<EvidenceSignal>
        {
            new("SeverityScore", "Composite leak severity score", inst.SeverityScore)
        };
        if (inst.DuplicateSubscriptionCount > 0)
            signals.Add(new EvidenceSignal("DuplicateSubscriptionCount", "Subscribers registered more than once", inst.DuplicateSubscriptionCount));
        if (inst.IsDisposedButSubscribed)
            signals.Add(new EvidenceSignal("IsDisposedButSubscribed", "Subscriber implements IDisposable but is still subscribed", 1));
        if (inst.HasLifetimeMismatch)
            signals.Add(new EvidenceSignal("HasLifetimeMismatch", "Subscribers appear shorter-lived than the publisher", 1));
        return signals;
    }

    /// <summary>
    /// Evidence enrichment (design §4.2/§4.3, §9.19). Every instance is eligible for a root-path
    /// search attempt, processed in <paramref name="topLeakInstances"/>'s existing
    /// severity-descending order — a global wall-clock guard
    /// (<see cref="EventLeakOptions.MaxEvidenceEnrichmentMs"/>) bounds the total search time across
    /// the run; a per-instance guard skips the search entirely when a cheap RootHint is already
    /// known. Retargeted onto <see cref="IHeapRootPathQuery.TryFindRootPath"/>, replacing the
    /// pre-retyping analyzer's own <c>RootPathFinder</c>/<c>ReferenceGraph</c>/
    /// <c>cache.GetOrBuildValidRoots</c> usage directly — same retarget every prior batch's
    /// evidence-path search made.
    /// </summary>
    private static void PopulateEvidence(
        IHeapRootPathQuery rootPaths, List<EventLeakInstanceSnapshot> topLeakInstances,
        EventLeakOptions options, CancellationToken cancellationToken)
    {
        if (topLeakInstances.Count == 0)
            return;

        var budgetSw = System.Diagnostics.Stopwatch.StartNew();
        long maxMs = Math.Max(0, options.MaxEvidenceEnrichmentMs);

        for (int i = 0; i < topLeakInstances.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EventLeakInstanceSnapshot inst = topLeakInstances[i];
            string? sampleSubscriberHint = string.IsNullOrWhiteSpace(inst.RootHint) ? null : inst.RootHint;
            List<EvidenceSignal> signals = BuildInstanceSignals(inst);

            // Global wall-clock budget exhausted: keep RootHint only, marked distinctly from
            // TryFindRootPath's own (BFS-internal) searchTruncated meaning.
            if (budgetSw.ElapsedMilliseconds > maxMs)
            {
                topLeakInstances[i] = inst with
                {
                    Evidence = new EventLeakEvidence(EventLeakEvidenceSchemaVersion, null, sampleSubscriberHint, true, signals)
                };
                continue;
            }

            // Skip-when-root-hint-exists guard: a publisher that's already a known direct root
            // gains nothing from a search.
            string? publisherRootPath = null;
            bool searchTruncated = false;
            if (string.IsNullOrEmpty(inst.RootHint))
            {
                bool found = rootPaths.TryFindRootPath(inst.PublisherAddress, RootPathOptions, out string? formattedPath, out searchTruncated);
                if (found)
                    publisherRootPath = formattedPath;
            }

            topLeakInstances[i] = inst with
            {
                Evidence = new EventLeakEvidence(EventLeakEvidenceSchemaVersion, publisherRootPath, sampleSubscriberHint, searchTruncated, signals)
            };
        }
    }

    /// <summary>Average size (bytes) per type, replacing the pre-retyping analyzer's own
    /// <c>IHeapAnalysisCache.GetOrBuildTypeStatistics</c> direct call with the shared Tier-1
    /// <see cref="IHeapTypeStatisticsQuery"/> surface.</summary>
    private static Dictionary<string, ulong> BuildTypeSizeMap(IHeapTypeStatisticsQuery typeStatistics)
    {
        IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();
        var map = new Dictionary<string, ulong>(stats.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, HeapTypeStatistics> kvp in stats)
        {
            if (kvp.Value.InstanceCount > 0)
                map[kvp.Key] = kvp.Value.TotalSize / (ulong)kvp.Value.InstanceCount;
        }
        return map;
    }

    // Tier 1 retained bytes (design §4.4, audit #3): fold over AllSubscriberTypeCounts,
    // accumulated across ALL instances in the group during the scan (see AddToAccumulator), not
    // just the capped Instances/TopInstances list. This is TotalSubscribers × avgSubscriberSizeByMT
    // expressed as an exact weighted sum rather than a separate average-then-multiply step.
    // Internal so accuracy tests can exercise the fold against a hand-built EventGroupInfo fixture
    // without a heap.
    internal static ulong EstimateGroupRetainedBytes(EventGroupInfo g, Dictionary<string, ulong> typeSizeMap)
    {
        Dictionary<string, int>? typeCounts = g.AllSubscriberTypeCounts;
        if (typeCounts is null || typeCounts.Count == 0)
            return 0;

        ulong total = 0;
        foreach (KeyValuePair<string, int> kvp in typeCounts)
        {
            ulong sz = typeSizeMap.TryGetValue(kvp.Key, out ulong s) ? s : 64UL;
            total += sz * (ulong)kvp.Value;
        }
        return total;
    }

    // P3-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): pure string-pattern
    // classification over data already extracted during the scan (PublisherType,
    // EventFieldName) — no new heap reads, no new IPublisherShape. Unstopped/undisposed
    // timers are one of the most common sources of process-lifetime event leaks in
    // production .NET (audit Area 1 gap #3).
    internal static bool IsTimerEvent(string publisherType, string eventFieldName)
    {
        if (string.Equals(publisherType, "System.Timers.Timer", StringComparison.Ordinal))
            return string.Equals(eventFieldName, "Elapsed", StringComparison.Ordinal);
        if (string.Equals(publisherType, "System.Windows.Forms.Timer", StringComparison.Ordinal))
            return string.Equals(eventFieldName, "Tick", StringComparison.Ordinal);
        if (string.Equals(publisherType, "System.Windows.Threading.DispatcherTimer", StringComparison.Ordinal))
            return string.Equals(eventFieldName, "Tick", StringComparison.Ordinal);
        return false;
    }

    // INotifyPropertyChanged.PropertyChanged is the highest-frequency MVVM event and the
    // most commonly retained event across WPF/Blazor applications (audit Area 1 gap #4).
    // Any type can implement the interface, so this is a name-only match — there is no
    // single "publisher type" to key off the way IsTimerEvent does.
    internal static bool IsPropertyChangedEvent(string eventFieldName) =>
        string.Equals(eventFieldName, "PropertyChanged", StringComparison.Ordinal);

    public void Dispose() { }
}
