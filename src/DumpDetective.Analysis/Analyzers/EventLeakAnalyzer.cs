using DumpDetective.Analysis.Analyzers.EventLeak;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

namespace DumpDetective.Analysis.Analyzers
{
    // IRequiresDominatorTreeIndex (§10.3): opts this analyzer into Stage B's exact dominator
    // tree build gate (DiskBackedObjectIndexWriter.Build's buildStageB) so
    // cache.TryGetDominatorTreeProvider() in Analyze() below is reliably non-null instead of
    // depending on some other analyzer (DominatorAnalyzer, GCRootAnalyzer, etc.) also being
    // active in the same run to have implicitly requested it.
    public class EventLeakAnalyzer : IAnalyzer, IHeapIndexScanParticipant, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
    {
        // Presentation and severity tuning moved to EventLeakOptions

        private readonly ILogger<EventLeakAnalyzer>? _logger;

        public EventLeakAnalyzer() { }

        public EventLeakAnalyzer(ILogger<EventLeakAnalyzer>? logger)
        {
            _logger = logger;
        }

        // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
        // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
        // OnHeapEntry; consumed by FindEventLeaks once the shared index scan has completed.
        private EventLeakFastScanner? _participantFastScanner;
        private PublisherRegistry? _participantRegistry;
        private Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>? _participantGroupAcc;
        // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): distinct publisher MTs that
        // produced at least one accepted leak, tracked by AddToAccumulator — compared against
        // PublisherRegistry.CandidatePublisherCount to report "types scanned, zero leaking".
        private HashSet<ulong>? _participantLeakingMTs;
        private Dictionary<ulong, string>? _participantRootHints;
        private IReadOnlyList<ClrAppDomain>? _participantAppDomains;
        private List<(ulong addr, ulong mt, ulong delegateAddr)>? _participantBuf;
        private EventLeakOptions? _participantOptions;
        private int _participantEventsScanned;
        private int _participantPublisherInstances;
        // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
        // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
        // shared scan run" from a second cache.TryGetHeapIndex call in FindEventLeaks.
        private bool _participantScanSucceeded;

        public string Name => "Event Leak Analysis";
        public string Category => "Events";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EventLeakOptions options = context.AnalysisOptions.EventLeak;

            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress, cancellationToken).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, EventLeakOptions options)
        {
            return Analyze(heap, cache: null, options, progress: null, CancellationToken.None);
        }

        // Resets per-entry accumulator fields ahead of the shared heap-index scan pass.
        // Explicit interface implementations: EventLeakAnalyzer is public but HeapEntry is
        // internal, so these members must not be exposed on the public API surface.
        void IHeapIndexScanParticipant.BeforeHeapIndexScan(AnalysisContext context)
        {
            _participantScanSucceeded = false;
            _participantOptions = context.AnalysisOptions.EventLeak;
            _participantGroupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>();
            _participantLeakingMTs = new HashSet<ulong>();
            _participantAppDomains = context.Heap.Runtime.AppDomains;
            context.ReportSubPhase?.Invoke("building root hint map");
            var __rootHintSw = System.Diagnostics.Stopwatch.StartNew();
            _participantRootHints = BuildRootHintMap(context.Heap, context.Cache);
            _logger?.LogDebug("EventLeakAnalyzer.BuildRootHintMap: {ElapsedSeconds:F2}s", __rootHintSw.Elapsed.TotalSeconds);
            context.ReportSubPhase?.Invoke("building publisher registry");
            var __registrySw = System.Diagnostics.Stopwatch.StartNew();
            // No CancellationToken to thread through here — IHeapIndexScanParticipant.BeforeHeapIndexScan
            // doesn't carry one (AnalysisContext has none either); widening that shared interface for
            // every participant is out of scope for this fix. The FindEventLeaks call site below does
            // have one and passes it through.
            _participantRegistry = PublisherRegistry.Build(context.Heap, context.Cache);
            _logger?.LogDebug("EventLeakAnalyzer.PublisherRegistry.Build: {ElapsedSeconds:F2}s", __registrySw.Elapsed.TotalSeconds);
            var __fastScannerSw = System.Diagnostics.Stopwatch.StartNew();
            _participantFastScanner = new EventLeakFastScanner(context.Heap, _participantRegistry, context.Progress);
            _logger?.LogDebug("EventLeakAnalyzer.new EventLeakFastScanner: {ElapsedSeconds:F2}s", __fastScannerSw.Elapsed.TotalSeconds);

            _participantBuf = new List<(ulong addr, ulong mt, ulong delegateAddr)>(capacity: 64);
            _participantEventsScanned = 0;
            _participantPublisherInstances = 0;
        }

        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        {
            if (_participantFastScanner is null)
                return;

            _participantFastScanner.ScanEntry(
                in entry, _participantBuf!, _participantGroupAcc!, _participantRootHints!,
                _participantOptions!, _participantLeakingMTs!, ref _participantEventsScanned, ref _participantPublisherInstances);
        }

        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)
        {
            _participantScanSucceeded = succeeded;
            if (_participantFastScanner is not null)
            {
                double processMs = _participantFastScanner.GetScanTimings();
                _logger?.LogDebug(
                    "EventLeakFastScanner.ProcessPublisherEntry (per-object): {ProcessSeconds:F2}s",
                    processMs / 1000.0);
            }
        }

        // internal so EventLeakFastScanner can reference the type without reflection.
        // P3-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): fixed, ordered subscriber-count
        // buckets for the cross-group distribution histogram — distinguishes "one giant leaking
        // publisher" from "many small leaks adding up". Small and allocation-free per group
        // (plain int[] indexed by bucket, not a Dictionary); GetSubscriberCountBucketIndex is an
        // O(BucketCount) linear scan over 8 entries, the only per-leak cost.
        internal static readonly (string Label, int MaxInclusive)[] SubscriberCountHistogramBuckets =
        [
            ("1", 1),
            ("2", 2),
            ("3-5", 5),
            ("6-10", 10),
            ("11-25", 25),
            ("26-50", 50),
            ("51-100", 100),
            ("101+", int.MaxValue)
        ];

        internal static int GetSubscriberCountBucketIndex(int subscriberCount)
        {
            for (int i = 0; i < SubscriberCountHistogramBuckets.Length; i++)
                if (subscriberCount <= SubscriberCountHistogramBuckets[i].MaxInclusive)
                    return i;
            return SubscriberCountHistogramBuckets.Length - 1;
        }

        internal class GroupAccumulator
        {
            public int InstanceCount;
            public int TotalSubscribers;
            public int MinSubscribers = int.MaxValue;
            public int MaxSubscribers = 0;
            public int MaxSeverity = 0;
            public List<EventLeakInfo> TopInstances = new List<EventLeakInfo>();
            // Subscriber type counts aggregated across ALL instances (not just TopInstances).
            // TopInstances is capped at TopDetailedInstancesPerGroup; without this dict the
            // per-type breakdown in the report only reflects those few stored instances.
            public Dictionary<string, int> AllSubscriberTypeCounts = new(StringComparer.Ordinal);
            // Handler-method counts aggregated across ALL instances (design §7 / Phase 5 correlation).
            // Populated in the same loop as AllSubscriberTypeCounts — MethodName is already resident
            // on SubscriberInfo, just discarded there.
            public Dictionary<(string Type, string? MethodName), int> AllSubscriberMethodCounts = new();
            // P3-4: subscriber-count histogram, aggregated across ALL instances (not just TopInstances)
            // for the same reason as the two dictionaries above.
            public int[] SubscriberCountBuckets = new int[SubscriberCountHistogramBuckets.Length];
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            var groupedLeaks = FindEventLeaks(heap, cache, options, progress, cancellationToken,
                out int eventsScanned, out int publisherInstances,
                out int candidatePublisherTypeCount, out int leakingPublisherTypeCount);
            _logger?.LogDebug("EventLeakAnalyzer.FindEventLeaks: {ElapsedSeconds:F2}s", __sw.Elapsed.TotalSeconds);

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
            __sw.Restart();
            Dictionary<string, ulong> typeSizeMap = BuildTypeSizeMap(heap, cache);
            _logger?.LogDebug("EventLeakAnalyzer.BuildTypeSizeMap: {ElapsedSeconds:F2}s", __sw.Elapsed.TotalSeconds);
            __sw.Restart();

            // Back-fill SubscriberSize on stored instances now that typeSizeMap is available.
            // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): prefer the
            // exact dominator tree's per-subscriber retained bytes over the per-type shallow-size
            // average — the addresses here are real (only the capped Instances list, not the
            // AllSubscriberTypeCounts fold used by EstimateGroupRetainedBytes, which has no
            // addresses to look up).
            IDominatorTreeProvider? eventLeakTreeProvider = cache?.TryGetDominatorTreeProvider();
            for (int i = 0; i < groupedLeaks.Count; i++)
                foreach (EventLeakInfo inst in groupedLeaks[i].Instances)
                    foreach (SubscriberInfo s in inst.Subscribers)
                    {
                        if (s.SubscriberSize != 0)
                            continue;

                        if (eventLeakTreeProvider is not null && eventLeakTreeProvider.TryGetRetainedBytes(s.Address, out ulong exactBytes))
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

            _logger?.LogDebug("EventLeakAnalyzer.BuildSnapshots: {ElapsedSeconds:F2}s", __sw.Elapsed.TotalSeconds);
            __sw.Restart();
            // Every instance is enrichment-eligible now (§9.19) — topLeakInstances is already
            // sorted by severity descending, so MaxEvidenceEnrichmentMs's wall-clock budget alone
            // governs how much of the priority-ordered work actually completes.
            PopulateEvidence(heap, cache, topLeakInstances, options, _logger);
            _logger?.LogDebug("EventLeakAnalyzer.PopulateEvidence: {ElapsedSeconds:F2}s", __sw.Elapsed.TotalSeconds);

            __sw.Restart();
            var topSubscriberTypesAcrossGroups = BuildTopSubscriberTypesAcrossGroups(groupedLeaks, TopCorrelationEntries);
            var topHandlerMethodsAcrossGroups = BuildTopHandlerMethodsAcrossGroups(groupedLeaks, TopCorrelationEntries);
            var subscriberCountHistogram = BuildSubscriberCountHistogram(groupedLeaks);
            _logger?.LogDebug("EventLeakAnalyzer.BuildCorrelationViews: {ElapsedSeconds:F2}s", __sw.Elapsed.TotalSeconds);

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

        // Cap on the correlation views (design §7) — first-class cross-group folds, not a
        // per-group appendix; bounded so the report table stays scannable.
        private const int TopCorrelationEntries = 20;

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
            var totals = new int[SubscriberCountHistogramBuckets.Length];
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
                result.Add(new NameCountEntry(SubscriberCountHistogramBuckets[b].Label, totals[b]));
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

        // Schema for EventLeakEvidence — bump when the shape or its population rules change.
        private const int EventLeakEvidenceSchemaVersion = 1;

        /// <summary>
        /// Evidence enrichment (design §4.2/§4.3, §9.19). Every instance is eligible for a
        /// root-path BFS attempt, processed in <paramref name="topLeakInstances"/>'s existing
        /// severity-descending order — a global wall-clock guard
        /// (<see cref="EventLeakOptions.MaxEvidenceEnrichmentMs"/>) bounds the total BFS time
        /// across the run, and a per-instance guard skips the BFS entirely when a cheap RootHint
        /// is already known.
        /// </summary>
        private static void PopulateEvidence(
            ClrHeap heap, IHeapAnalysisCache? cache, List<EventLeakInstanceSnapshot> topLeakInstances,
            EventLeakOptions options, ILogger<EventLeakAnalyzer>? logger)
        {
            if (cache is null || topLeakInstances.Count == 0)
                return;

            var __evSw = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            logger?.LogDebug("PopulateEvidence.GetOrBuildValidRoots: {ElapsedSeconds:F2}s ({RootCount} roots)", __evSw.Elapsed.TotalSeconds, roots.Count);

            var provider = new ReferenceGraph(heap);
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = 5_000,
                MaxCandidateDepth = 8,
                MaxRootExpansionDepth = 12,
                LargeFanoutThreshold = 100,
            };
            var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false, cache.TryGetReverseIndexProvider(), cache);

            var budgetSw = System.Diagnostics.Stopwatch.StartNew();
            long maxMs = Math.Max(0, options.MaxEvidenceEnrichmentMs);

            __evSw.Restart();
            long __pathTicks = 0;
            int __truncated = 0, __foundCount = 0, __enrichAttempted = 0, __budgetExhausted = 0;
            for (int i = 0; i < topLeakInstances.Count; i++)
            {
                EventLeakInstanceSnapshot inst = topLeakInstances[i];
                string? sampleSubscriberHint = string.IsNullOrWhiteSpace(inst.RootHint) ? null : inst.RootHint;
                List<EvidenceSignal> signals = BuildInstanceSignals(inst);

                // Global wall-clock budget exhausted: keep RootHint only, marked distinctly from
                // RootPathFinder's own (BFS-internal) searchTruncated meaning.
                if (budgetSw.ElapsedMilliseconds > maxMs)
                {
                    __budgetExhausted++;
                    topLeakInstances[i] = inst with
                    {
                        Evidence = new EventLeakEvidence(EventLeakEvidenceSchemaVersion, null, sampleSubscriberHint, true, signals)
                    };
                    continue;
                }

                // Skip-when-root-hint-exists guard: a publisher that's already a known direct
                // root gains nothing from a BFS.
                string? publisherRootPath = null;
                if (string.IsNullOrEmpty(inst.RootHint))
                {
                    __enrichAttempted++;
                    long __t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool found = finder.TryFindAnyRootPath(inst.PublisherAddress, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
                    __pathTicks += System.Diagnostics.Stopwatch.GetTimestamp() - __t0;
                    if (found) __foundCount++;
                    if (searchTruncated) __truncated++;
                    if (found)
                        publisherRootPath = RootPathSearchSupport.FormatPath(heap, rootKind!, addresses, cache);
                }

                topLeakInstances[i] = inst with
                {
                    Evidence = new EventLeakEvidence(EventLeakEvidenceSchemaVersion, publisherRootPath, sampleSubscriberHint, false, signals)
                };
            }

            double __pathSec = __pathTicks * 1.0 / System.Diagnostics.Stopwatch.Frequency;
            logger?.LogDebug(
                "PopulateEvidence.RootPathLoop: {ElapsedSeconds:F2}s over {InstanceCount} instances ({EnrichAttempted} BFS attempts, TryFindAnyRootPath {PathSeconds:F2}s, found={FoundCount}, truncated={TruncatedCount}, budgetExhausted={BudgetExhaustedCount})",
                __evSw.Elapsed.TotalSeconds, topLeakInstances.Count, __enrichAttempted, __pathSec, __foundCount, __truncated, __budgetExhausted);
        }

        private static Dictionary<string, ulong> BuildTypeSizeMap(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            if (cache is not HeapAnalysisCache heapCache)
                return new Dictionary<string, ulong>(0, StringComparer.Ordinal);

            Dictionary<string, CachedTypeStatistics> typeStats = heapCache.GetOrBuildTypeStatistics(heap);
            var map = new Dictionary<string, ulong>(typeStats.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, CachedTypeStatistics> kvp in typeStats)
            {
                if (kvp.Value.Count > 0)
                    map[kvp.Key] = kvp.Value.TotalSize / (ulong)kvp.Value.Count;
            }
            return map;
        }

        // Tier 1 retained bytes (design §4.4, audit #3): fold over AllSubscriberTypeCounts,
        // which is accumulated across ALL instances in the group during the scan (see
        // AddToAccumulator), not just the capped Instances/TopInstances list. This is
        // TotalSubscribers × avgSubscriberSizeByMT expressed as an exact weighted sum rather
        // than a separate average-then-multiply step. Internal so accuracy tests can exercise
        // the fold against a hand-built EventGroupInfo fixture without a heap.
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

        /// <summary>
        /// Returns true if <paramref name="type"/> declares at least one instance or static field
        /// whose base type is <c>System.MulticastDelegate</c> (i.e. a delegate / event backing field).
        /// Called once per unique MethodTable to populate the MT pre-filter cache.
        /// </summary>
        private static bool HasDelegateFields(ClrType type)
        {
            foreach (ClrInstanceField field in type.Fields)
            {
                if (TypeFilterHelper.IsDelegateType(field.Type))
                    return true;
            }
            foreach (ClrStaticField field in type.StaticFields)
            {
                if (TypeFilterHelper.IsDelegateType(field.Type))
                    return true;
            }
            return false;
        }

        // internal so EventLeakFastScanner can call without reflection.
        // leakingMTs is the single choke point every accepted leak (instance or static) passes
        // through, so it's the natural place to mark a publisher MT as "leaking" for P2-2's
        // clean-vs-leaking count (docs/analysis/phase1/eventleak-analyzer-audit.md). Optional so
        // callers that don't care about that count (tests) don't need to thread a set through.
        internal static void AddToAccumulator(
            Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator> acc,
            EventLeakInfo leak,
            int capacity,
            HashSet<ulong>? leakingMTs = null)
        {
            leakingMTs?.Add(leak.PublisherMethodTable);

            var key = (leak.PublisherType, leak.EventFieldName, leak.IsStatic);
            if (!acc.TryGetValue(key, out var a))
            {
                a = new GroupAccumulator();
                acc[key] = a;
            }

            a.InstanceCount++;
            a.TotalSubscribers += leak.SubscriberCount;
            if (leak.SubscriberCount < a.MinSubscribers) a.MinSubscribers = leak.SubscriberCount;
            if (leak.SubscriberCount > a.MaxSubscribers) a.MaxSubscribers = leak.SubscriberCount;
            if (leak.SeverityScore > a.MaxSeverity) a.MaxSeverity = leak.SeverityScore;
            a.SubscriberCountBuckets[GetSubscriberCountBucketIndex(leak.SubscriberCount)]++;

            // Accumulate subscriber type counts from ALL instances so the report type-breakdown
            // reflects the full population, not just the top-N stored instances.
            foreach (SubscriberInfo s in leak.Subscribers)
            {
                a.AllSubscriberTypeCounts.TryGetValue(s.Type, out int typeCount);
                a.AllSubscriberTypeCounts[s.Type] = typeCount + 1;

                var methodKey = (s.Type, s.MethodName);
                a.AllSubscriberMethodCounts.TryGetValue(methodKey, out int methodCount);
                a.AllSubscriberMethodCounts[methodKey] = methodCount + 1;
            }

            var list = a.TopInstances;
            if (capacity <= 0) capacity = 1;
            if (list.Count < capacity)
            {
                list.Add(leak);
                return;
            }

            // Replace smallest subscriber-count instance if this one is larger
            int minIdx = 0;
            int minVal = list[0].SubscriberCount;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].SubscriberCount < minVal)
                {
                    minVal = list[i].SubscriberCount;
                    minIdx = i;
                }
            }
            if (leak.SubscriberCount > minVal)
            {
                list[minIdx] = leak;
            }
        }

        /// <summary>Merges one partial accumulator into the target dictionary (used by parallel scan merge step).</summary>
        internal static void MergeAccumulatorEntry(
            Dictionary<(string, string, bool), GroupAccumulator> target,
            (string, string, bool) key,
            GroupAccumulator source,
            int capacity)
        {
            if (!target.TryGetValue(key, out GroupAccumulator? dest))
            {
                dest = new GroupAccumulator();
                target[key] = dest;
            }

            dest.InstanceCount += source.InstanceCount;
            dest.TotalSubscribers += source.TotalSubscribers;
            if (source.MinSubscribers < dest.MinSubscribers) dest.MinSubscribers = source.MinSubscribers;
            if (source.MaxSubscribers > dest.MaxSubscribers) dest.MaxSubscribers = source.MaxSubscribers;
            if (source.MaxSeverity > dest.MaxSeverity) dest.MaxSeverity = source.MaxSeverity;

            for (int i = 0; i < SubscriberCountHistogramBuckets.Length; i++)
                dest.SubscriberCountBuckets[i] += source.SubscriberCountBuckets[i];

            foreach (var kvp in source.AllSubscriberTypeCounts)
            {
                dest.AllSubscriberTypeCounts.TryGetValue(kvp.Key, out int existing);
                dest.AllSubscriberTypeCounts[kvp.Key] = existing + kvp.Value;
            }

            foreach (var kvp in source.AllSubscriberMethodCounts)
            {
                dest.AllSubscriberMethodCounts.TryGetValue(kvp.Key, out int existing);
                dest.AllSubscriberMethodCounts[kvp.Key] = existing + kvp.Value;
            }

            foreach (EventLeakInfo inst in source.TopInstances)
            {
                if (dest.TopInstances.Count < capacity)
                {
                    dest.TopInstances.Add(inst);
                }
                else
                {
                    int minIdx = 0;
                    int minVal = dest.TopInstances[0].SubscriberCount;
                    for (int i = 1; i < dest.TopInstances.Count; i++)
                    {
                        if (dest.TopInstances[i].SubscriberCount < minVal)
                        {
                            minVal = dest.TopInstances[i].SubscriberCount;
                            minIdx = i;
                        }
                    }
                    if (inst.SubscriberCount > minVal)
                        dest.TopInstances[minIdx] = inst;
                }
            }
        }

        /// <summary>
        /// Converts the heap's ClrObject stream into <see cref="HeapEntry"/> structs
        /// so the fast scanner can consume a uniform entry format even without a pre-built index.
        /// </summary>
        private static IEnumerable<HeapEntry> StreamObjectsAsEntries(ClrHeap heap)
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (mt == 0) continue;
                yield return new HeapEntry(obj.Address, mt, obj.Size);
            }
        }

        private static void AddFindings(List<InsightFinding> findings, List<EventGroupInfo> groupedLeaks)
        {
            int findingsToEmit = Math.Min(5, groupedLeaks.Count);
            for (int i = 0; i < findingsToEmit; i++)
            {
                var group = groupedLeaks[i];
                var severity = group.SeverityScore >= 35
                    ? FindingSeverity.Critical
                    : group.SeverityScore >= 20
                        ? FindingSeverity.Warning
                        : FindingSeverity.Info;

                findings.Add(new InsightFinding(
                    Analyzer: nameof(EventLeakAnalyzer),
                    Category: "Leak",
                    Severity: severity,
                    Title: $"Potential {(group.IsStatic ? "static" : "instance")} event retention in {group.PublisherType}.{group.EventFieldName}",
                    Evidence: $"{group.InstanceCount:N0} publisher instance(s), {group.TotalSubscribers:N0} total subscribers, max {group.MaxSubscribers:N0} per instance.",
                    Recommendation: "Ensure subscribers are unsubscribed and avoid long-lived static event publishers where possible.",
                    Tags: group.IsStatic
                        ? ["event-leak", "static-event", "retention"]
                        : ["event-leak", "instance-event", "retention"],
                    MetricValue: group.TotalSubscribers,
                    MetricUnit: "subscribers"));
            }
        }

        private List<EventGroupInfo> FindEventLeaks(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken, out int eventsScanned, out int publisherInstances, out int candidatePublisherTypeCount, out int leakingPublisherTypeCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            eventsScanned = 0;
            publisherInstances = 0;
            var appDomains = heap.Runtime.AppDomains;
            var scanCounter = new ObjectScanCounter("scanning event handlers", progress);

            Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator> groupAcc;
            Dictionary<ulong, string> rootHints;
            PublisherRegistry registry;
            // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): distinct publisher MTs
            // marked by AddToAccumulator as having produced at least one accepted leak — see
            // that method's leakingMTs parameter for where entries are added.
            HashSet<ulong> leakingMTs;

            // BeforeHeapIndexScan/OnHeapEntry already ran via the pipeline's
            // HeapIndexScanDispatcher before AnalyzeAsync executes when a disk-backed heap
            // index exists. Reuse that participant-accumulated state instead of re-scanning
            // the index. If the shared scan failed partway (isolated by the dispatcher),
            // fall back to a fresh scan instead of trusting partial state.
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _)
                && _participantScanSucceeded && _participantGroupAcc is not null)
            {
                groupAcc = _participantGroupAcc;
                rootHints = _participantRootHints!;
                registry = _participantRegistry!;
                leakingMTs = _participantLeakingMTs!;
                eventsScanned = _participantEventsScanned;
                publisherInstances = _participantPublisherInstances;
            }
            else
            {
                groupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>();
                leakingMTs = new HashSet<ulong>();
                var __phaseSw = System.Diagnostics.Stopwatch.StartNew();
                rootHints = BuildRootHintMap(heap, cache);
                _logger?.LogDebug("FindEventLeaks.BuildRootHintMap: {ElapsedSeconds:F2}s ({RootCount} roots)", __phaseSw.Elapsed.TotalSeconds, rootHints.Count);

                __phaseSw.Restart();
                registry = PublisherRegistry.Build(heap, cache, cancellationToken: cancellationToken);
                _logger?.LogDebug("FindEventLeaks.PublisherRegistry.Build: {ElapsedSeconds:F2}s", __phaseSw.Elapsed.TotalSeconds);

                // ── Fast scanner: direct IMemoryReader.ReadPointer — no heap.GetObject ────
                __phaseSw.Restart();
                var fastScanner = new EventLeakFastScanner(heap, registry, progress);
                _logger?.LogDebug("FindEventLeaks.new EventLeakFastScanner: {ElapsedSeconds:F2}s", __phaseSw.Elapsed.TotalSeconds);

                IEnumerable<HeapEntry> streamingEntries = cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)
                    ? hc.EnumerateIndexedEntries()
                    : StreamObjectsAsEntries(heap);

                __phaseSw.Restart();
                fastScanner.Scan(streamingEntries, groupAcc, rootHints, options, leakingMTs,
                    ref eventsScanned, ref publisherInstances);
                double __scanWall = __phaseSw.Elapsed.TotalSeconds;

                double freshProcessMs = fastScanner.GetScanTimings();
                _logger?.LogDebug(
                    "FindEventLeaks.Scan WALL: {ScanWallSeconds:F2}s  (ProcessPublisherEntry {ProcessSeconds:F2}s + unattributed {UnattributedSeconds:F2}s)",
                    __scanWall, freshProcessMs / 1000.0, __scanWall - freshProcessMs / 1000.0);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Statics run exactly once here — the single registry-driven sweep (design §6).
            // No longer processed on the hot path, so there is nothing left to dedup against.
            var __sweepSw = System.Diagnostics.Stopwatch.StartNew();
            SweepRegistryStatics(heap, appDomains, rootHints, options, registry,
                leak => AddToAccumulator(groupAcc, leak, options.TopDetailedInstancesPerGroup, leakingMTs), ref eventsScanned, cancellationToken, progress);
            _logger?.LogDebug("FindEventLeaks.SweepRegistryStatics: {ElapsedSeconds:F2}s", __sweepSw.Elapsed.TotalSeconds);

            candidatePublisherTypeCount = registry.CandidatePublisherCount;
            leakingPublisherTypeCount = leakingMTs.Count;

            scanCounter.Complete();

            // Convert accumulators into EventGroupInfo list
            __sweepSw.Restart();
            var result = new List<EventGroupInfo>(groupAcc.Count);
            foreach (var kvp in groupAcc)
            {
                var key = kvp.Key;
                var acc = kvp.Value;
                int instanceCount = acc.InstanceCount;
                int totalSubs = acc.TotalSubscribers;
                int minSubs = acc.MinSubscribers == int.MaxValue ? 0 : acc.MinSubscribers;
                int maxSubs = acc.MaxSubscribers;
                int maxSeverity = acc.MaxSeverity;

                // sort top instances by severity desc then subscriber count
                acc.TopInstances.Sort((a, b) =>
                {
                    int cmp = b.SeverityScore.CompareTo(a.SeverityScore);
                    return cmp != 0 ? cmp : b.SubscriberCount.CompareTo(a.SubscriberCount);
                });

                result.Add(new EventGroupInfo
                {
                    PublisherType = key.PublisherType,
                    EventFieldName = key.EventFieldName,
                    IsStatic = key.IsStatic,
                    SeverityScore = maxSeverity,
                    InstanceCount = instanceCount,
                    TotalSubscribers = totalSubs,
                    AverageSubscribers = instanceCount == 0 ? 0.0 : (double)totalSubs / instanceCount,
                    MaxSubscribers = maxSubs,
                    MinSubscribers = minSubs,
                    Instances = acc.TopInstances,
                    AllSubscriberTypeCounts = acc.AllSubscriberTypeCounts,
                    AllSubscriberMethodCounts = acc.AllSubscriberMethodCounts,
                    SubscriberCountBuckets = acc.SubscriberCountBuckets
                });
            }

            // Sort by total subscribers
            result.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));
            _logger?.LogDebug("FindEventLeaks.BuildGroupInfos+Sort: {ElapsedSeconds:F2}s ({GroupCount} groups)", __sweepSw.Elapsed.TotalSeconds, result.Count);

            return result;
        }

        // internal so EventLeakFastScanner can call it directly.
        internal static List<SubscriberInfo> GetStaticEventSubscribers(ClrHeap heap, ClrStaticField field, IReadOnlyList<ClrAppDomain> appDomains)
        {
            var subscribers = new List<SubscriberInfo>();
            // Deduplicate at the DELEGATE-OBJECT level only: it is theoretically possible for two
            // app domains to read the same delegate instance from a shared static field, in which
            // case enumerating both would double-count its subscriptions.
            // We do NOT deduplicate subscriber addresses across domains. In a multi-domain process
            // each app domain owns its own copy of the static delegate chain. The same subscriber
            // object (same heap address) subscribed in domain 1 AND domain 2 represents two
            // independent GC retention paths — both must be counted. The previous per-subscriber
            // deduplication was collapsing ~6 domains × N subscriptions down to just N, explaining
            // the ~6× undercount vs tools that count per-domain subscriptions correctly.
            var seenDelegateAddresses = new HashSet<ulong>(capacity: appDomains.Count);

            foreach (var appDomain in appDomains)
            {
                try
                {
                    ClrObject eventDelegate = field.ReadObject(appDomain);
                    if (!eventDelegate.IsValid)
                        continue;

                    // Heuristic filters for static fields as well.
                    if (eventDelegate.Type != null && IsNoiseType(eventDelegate.Type.Name ?? string.Empty))
                        continue;

                    // NOTE: do NOT re-apply LooksLikeEventField here.
                    // All callers (SweepRegistryStatics) already validated the field name before
                    // calling this method. Re-filtering here silently drops fields whose names are
                    // in the type's event set (e.g. "DataReceived", "Completed") but don't match
                    // the heuristic patterns.

                    // Skip if this exact delegate object was already processed from another domain.
                    if (!seenDelegateAddresses.Add(eventDelegate.Address))
                        continue;

                    var domainSubscribers = ExtractSubscribersFromDelegateAddress(heap, eventDelegate.Address);
                    foreach (var subscriber in domainSubscribers)
                    {
                        if (subscriber.Address != 0)
                            subscribers.Add(subscriber);
                    }
                }
                catch
                {
                }
            }

            return subscribers;
        }

        // internal so EventLeakFastScanner can call it directly.
        internal static EventLeakInfo CreateLeakInfo(
            ulong publisherAddress,
            string publisherType,
            string eventFieldName,
            bool isStatic,
            List<SubscriberInfo> subscribers,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            ClrHeap? heap = null,
            string? preferredRootHint = null,
            int publisherGeneration = -1,
            bool hasLifetimeMismatch = false,
            Dictionary<ulong, bool>? disposableTypeCache = null,
            ulong publisherMethodTable = 0)
        {
            string rootHint = preferredRootHint ?? string.Empty;

            if (string.IsNullOrEmpty(rootHint))
            {
                foreach (var subscriber in subscribers)
                {
                    if (rootHints.TryGetValue(subscriber.Address, out var hint))
                    {
                        rootHint = hint;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(rootHint) && publisherAddress != 0 && rootHints.TryGetValue(publisherAddress, out var publisherHint))
            {
                rootHint = publisherHint;
            }

            int duplicateCount = CountDuplicateSubscriptions(subscribers);
            bool isDisposedButSubscribed = heap != null && disposableTypeCache != null
                && HasDisposableSubscriber(heap, subscribers, disposableTypeCache);

            bool hasLowIncoming = false;
            // PERF GUARD: heap-scan per subscriber is O(N×M) on large dumps.
            // Only run when explicitly opted-in via options.
            if (heap != null && options.EnableLowIncomingRefsCheck)
            {
                try { hasLowIncoming = HasLowIncomingRefsSignal(heap, subscribers, options); }
                catch { hasLowIncoming = false; }
            }

            return new EventLeakInfo
            {
                PublisherAddress = publisherAddress,
                PublisherMethodTable = publisherMethodTable,
                PublisherType = publisherType,
                EventFieldName = eventFieldName,
                IsStatic = isStatic,
                SubscriberCount = subscribers.Count,
                Subscribers = subscribers,
                RootHint = rootHint,
                PublisherGeneration = publisherGeneration,
                DuplicateSubscriptionCount = duplicateCount,
                IsDisposedButSubscribed = isDisposedButSubscribed,
                HasLifetimeMismatch = hasLifetimeMismatch,
                HasLowIncomingRefs = hasLowIncoming,
                SeverityScore = CalculateSeverity(isStatic, subscribers.Count, rootHint, options,
                    publisherGeneration, duplicateCount, isDisposedButSubscribed, hasLifetimeMismatch, hasLowIncoming)
            };
        }

        internal static bool IsLikelyPublisher(EventLeakInfo leak, EventLeakOptions options)
        {
            if (leak == null) return false;

            // Must meet subscriber threshold
            if (leak.SubscriberCount < options.PublisherSubscriberThreshold) return false;

            // Static publishers are considered long-lived
            if (leak.IsStatic) return true;

            // Exclude compiler-generated / closure types regardless of generation
            if (TypeFilterHelper.IsCompilerGenerated(leak.PublisherType) || IsNoiseType(leak.PublisherType))
                return false;

            // Accept all non-noise publishers. Generation is a severity signal (bonus score),
            // not an acceptance gate — Gen0/Gen1 publishers can hold large subscriber chains too.
            return true;
        }

        internal static int CalculateSeverity(
            bool isStatic, int subscriberCount, string rootHint, EventLeakOptions options,
            int publisherGeneration = -1, int duplicateCount = 0, bool isDisposedButSubscribed = false, bool hasLifetimeMismatch = false,
            bool hasLowIncomingRefs = false)
        {
            int score = subscriberCount;
            score += (int)(Math.Log2(subscriberCount + 1) * options.SeveritySubscriberLogScale);
            if (isStatic) score += options.SeverityStaticPublisherBonus;
            if (!string.IsNullOrEmpty(rootHint)) score += options.SeverityRootHintBonus;
            if (publisherGeneration == 2) score += options.SeverityGen2PublisherBonus;
            if (duplicateCount > 0) score += options.SeverityDuplicateSubscriptionBonus;
            if (isDisposedButSubscribed) score += options.SeverityDisposedButSubscribedBonus;
            if (hasLifetimeMismatch) score += options.SeverityLifetimeMismatchBonus;
            if (hasLowIncomingRefs) score += options.SeverityLowIncomingRefsBonus;
            return score;
        }

        private static Dictionary<ulong, string> BuildRootHintMap(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var map = new Dictionary<ulong, string>();

            if (cache is not null)
            {
                var roots = cache.GetOrBuildValidRoots(heap);
                foreach ((string kind, ulong address) in roots)
                {
                    if (address == 0) continue;
                    map.TryAdd(address, kind);
                }
                return map;
            }

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (!root.Object.IsValid)
                    continue;

                ulong address = root.Object.Address;
                map.TryAdd(address, root.ToString() ?? root.RootKind.ToString());
            }

            return map;
        }

        /// <summary>
        /// Registry-driven statics pass (design §6). Iterates <see cref="PublisherRegistry.StaticPublisherMTs"/>
        /// once, reading static delegate fields at the offsets/names <see cref="PublisherRegistry.Build"/>
        /// already resolved (event-name matching happened once there, not per-call here). Statics
        /// no longer run on <see cref="EventLeakFastScanner"/>'s hot path, so this single sweep is the
        /// only place static event fields are counted — a type with both heap instances and a static
        /// event field is counted exactly once (closes the former double-count bug, where
        /// <c>SweepModuleStaticFields</c> accepted a <c>processedStaticMTs</c> dedup set but never
        /// consulted it).
        /// </summary>
        private static void SweepRegistryStatics(
            ClrHeap heap,
            IReadOnlyList<ClrAppDomain> appDomains,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            PublisherRegistry registry,
            Action<EventLeakInfo> addLeak,
            ref int eventsScanned,
            CancellationToken cancellationToken,
            IProgress<AnalyzerProgressReport>? progress = null)
        {
            var disposableTypeCache = registry.DisposableTypeCache;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int typesChecked = 0;

            foreach (ulong mt in registry.StaticPublisherMTs)
            {
                typesChecked++;
                if ((typesChecked & 8191) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (typesChecked % 500 == 0)
                    progress?.Report(new AnalyzerProgressReport(typesChecked,
                        "scanning static event fields",
                        $"{typesChecked:N0} types checked",
                        sw.Elapsed));

                if (!registry.TryGetDescriptors(mt, out EventFieldDescriptor[]? descriptors) || descriptors is null)
                    continue;

                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null) continue;

                foreach (EventFieldDescriptor descriptor in descriptors)
                {
                    if (!descriptor.IsStatic) continue;

                    ClrStaticField? sField = type.GetStaticFieldByName(descriptor.FieldName);
                    if (sField is null) continue;

                    eventsScanned++;

                    var subs = GetStaticEventSubscribers(heap, sField, appDomains);
                    if (subs.Count == 0) continue;

                    bool mismatch = CheckLifetimeMismatch(heap, 2, subs, options);
                    var leak = CreateLeakInfo(
                        publisherAddress: 0,
                        publisherType: type.Name ?? StringConstants.UnknownType,
                        eventFieldName: descriptor.FieldName,
                        isStatic: true,
                        subs, rootHints, options, heap: heap,
                        publisherGeneration: 2,
                        hasLifetimeMismatch: mismatch,
                        disposableTypeCache: disposableTypeCache,
                        publisherMethodTable: mt);

                    if (IsLikelyPublisher(leak, options))
                        addLeak(leak);
                }
            }
        }

        internal static void ParseRootPublisher(string rootDescription, out string publisherType, out string eventFieldName)
        {
            publisherType = "StaticRoot";
            eventFieldName = StringConstants.UnknownType;

            int lastDot = rootDescription.LastIndexOf('.');
            if (lastDot > 0 && lastDot < rootDescription.Length - 1)
            {
                publisherType = rootDescription[..lastDot].Trim();
                eventFieldName = rootDescription[(lastDot + 1)..].Trim();
            }
        }

        // internal so EventLeakFastScanner can reuse the same heuristic.
        // NOTE: this is only called after the field type has been confirmed as a delegate type,
        // so the _ prefix rule is safe from matching "any private field" — it's "any private
        // delegate-typed backing field". That alone is not enough to be event-specific, though:
        // see allowBareUnderscorePrefix below (P2-3, docs/analysis/phase1/eventleak-analyzer-audit.md).
        internal static bool LooksLikeEventFieldName(string? fieldName, bool allowBareUnderscorePrefix = true)
        {
            if (string.IsNullOrEmpty(fieldName)) return false;

            // common patterns and backing-field markers
            if (fieldName.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Changed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Handler", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Raised", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Fired", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.Contains("k__BackingField", StringComparison.Ordinal)) return true;
            // Standard C# explicit event backing field convention: _myEvent, _clickHandler, etc.
            // A bare "_" prefix alone is NOT event-specific — an ordinary callback/factory field
            // (_onComplete, _factory, _selector, _predicate) matches it just as well as a real
            // event backing field. allowBareUnderscorePrefix is false when the caller already
            // knows the type declares zero real events (PublisherRegistry's eventNames.Count == 0
            // path) — in that case only the stronger name-pattern signals above should qualify a
            // field, since there is no corroborating evidence this delegate field is an event at
            // all. When the type DOES declare at least one real event, the prefix is trusted as a
            // secondary signal for matching that event's (possibly non-standard-named) backing
            // field.
            if (allowBareUnderscorePrefix && fieldName.StartsWith("_", StringComparison.Ordinal)) return true;

            return false;
        }

        private static bool IsNoiseType(string typeName) => IsNoiseTypeName(typeName);

        // internal so EventLeakFastScanner can reuse the same heuristic.
        internal static bool IsNoiseTypeName(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            if (typeName.IndexOf("System.Threading", StringComparison.Ordinal) >= 0) return true;
            if (typeName.IndexOf("System.Linq", StringComparison.Ordinal) >= 0) return true;
            if (typeName.IndexOf("System.Threading.Tasks", StringComparison.Ordinal) >= 0) return true;
            if (typeName.Contains("<>", StringComparison.Ordinal)) return true;
            if (typeName.Contains("DisplayClass", StringComparison.Ordinal)) return true;
            return false;
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

        private static bool IsFieldBackedDelegate(ClrObject parent, ClrInstanceField field, ClrObject value)
        {
            try
            {
                if (field == null) return false;
                if (!field.IsObjectReference) return false;
                if (!value.IsValid || value.Type == null) return false;
                return TypeFilterHelper.IsDelegateType(value.Type);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pure logic core of <see cref="EventNameResolver.GetEventNames"/>: given two collections of method name
        /// tokens (the "add" side and the "remove" side), returns the set of event names that
        /// have a matching pair. Extracted so unit tests can exercise the logic without ClrMD.
        /// </summary>
        internal static HashSet<string> BuildEventNameSet(
            IEnumerable<string> addNames,
            IEnumerable<string> removeNames)
        {
            var removeSet = removeNames as HashSet<string>
                ?? new HashSet<string>(removeNames, StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in addNames)
                if (removeSet.Contains(e)) result.Add(e);
            return result;
        }

        // All C# delegates derive from MulticastDelegate. Dispatch is based on
        // whether _invocationList is a non-null array (multiple subscribers) vs null (single subscriber).
        private static List<SubscriberInfo> ExtractSubscribersFromDelegateAddress(ClrHeap heap, ulong delegateAddress, Dictionary<string, ulong>? typeSizeMap = null)
        {
            if (delegateAddress == 0)
                return [];

            ClrObject eventDelegate = heap.GetObject(delegateAddress);
            return ExtractSubscribersFromDelegateObject(heap.Runtime, eventDelegate, typeSizeMap);
        }

        private static List<SubscriberInfo> ExtractSubscribersFromDelegateObject(ClrRuntime runtime, ClrObject eventDelegate, Dictionary<string, ulong>? typeSizeMap)
        {
            var subscribers = new List<SubscriberInfo>();

            if (!eventDelegate.IsValid || eventDelegate.Type == null)
                return subscribers;

            foreach (var delegateObj in GetDelegateTargets(eventDelegate))
            {
                if (delegateObj.IsValid)
                    ExtractSingleSubscriber(runtime, delegateObj, subscribers, typeSizeMap);
            }

            return subscribers;
        }

        private static IEnumerable<ClrObject> GetDelegateTargets(ClrObject del)
        {
            if (!del.IsValid || del.Type == null)
                yield break;

            var invocationListField = DelegateHelper.GetCachedField(del.Type, StringConstants.DelegateInvocationListField);

            ClrObject invocationObj = invocationListField != null
                ? invocationListField.ReadObject(del, interior: false)
                : new ClrObject();

            if (invocationObj.IsValid)
            {
                if (invocationObj.IsArray)
                {
                    var arr = invocationObj.AsArray();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var item = arr.GetObjectValue(i);
                        if (item.IsValid)
                            yield return item;
                    }
                    yield break;
                }
                else if (invocationObj.Type?.BaseType?.Name == "System.MulticastDelegate")
                {
                    yield return invocationObj;
                    yield break;
                }
            }

            // fallback: single-cast uses the delegate object itself
            yield return del;
        }

        // Count incoming references to a target by sampling objects on the heap.
        // Scans up to `maxScan` objects and returns the observed count of incoming refs.
        internal static int CountIncomingRefs(ClrHeap heap, ulong targetAddress, int maxScan = 1000)
        {
            if (heap == null || targetAddress == 0) return 0;

            int seen = 0;
            int incoming = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (++seen > maxScan) break;
                if (!obj.IsValid || obj.Address == 0 || obj.Type == null) continue;

                try
                {
                    foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
                    {
                        if (child.IsValid && child.Address == targetAddress)
                        {
                            incoming++;
                            // early exit if multiple found in same object is irrelevant
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore problematic objects
                }
            }

            return incoming;
        }

        // Sample subscribers and return true if a significant fraction appear to have very few incoming refs.
        internal static bool HasLowIncomingRefsSignal(ClrHeap heap, List<SubscriberInfo> subscribers, EventLeakOptions options)
        {
            if (heap == null || subscribers == null || subscribers.Count == 0) return false;

            int probes = Math.Min(subscribers.Count, options.LifetimeMismatchProbeLimit);
            int lowCount = 0;
            int tried = 0;

            for (int i = 0; i < subscribers.Count && tried < probes; i++)
            {
                var s = subscribers[i];
                if (s.Address == 0) continue;
                tried++;
                int incoming = CountIncomingRefs(heap, s.Address, maxScan: 500);
                if (incoming <= 2) lowCount++;
            }

            if (tried == 0) return false;
            double frac = (double)lowCount / tried;
            return frac >= 0.25; // if >=25% of probed subscribers have very few inbound refs, flag
        }

        private static void ExtractSingleSubscriber(ClrRuntime runtime, ClrObject delegateObj, List<SubscriberInfo> subscribers, Dictionary<string, ulong>? typeSizeMap)
        {
            if (delegateObj.Type == null)
                return;

            var targetField = DelegateHelper.GetCachedField(delegateObj.Type, StringConstants.DelegateTargetField);
            if (targetField == null)
                return;

            string? methodName = ResolveMethodName(runtime, delegateObj);

            ClrObject target = targetField.ReadObject(delegateObj, interior: false);
            if (target.IsValid && target.Type != null)
            {
                var typeName = target.Type.Name ?? StringConstants.UnknownType;
                if (IsLikelySubscriber(typeName))
                {
                    subscribers.Add(new SubscriberInfo
                    {
                        Address = target.Address,
                        MethodTable = target.Type.MethodTable,
                        Type = typeName,
                        MethodName = methodName,
                        SubscriberSize = typeSizeMap != null && typeSizeMap.TryGetValue(typeName, out ulong sz) ? sz : 0
                    });
                }
            }
            else
            {
                // _target is null for static method handlers. The delegate object itself
                // has a unique heap address — use it so each static subscription is counted
                // and deduplicated correctly in GetStaticEventSubscribers.
                if (delegateObj.Address != 0)
                {
                    subscribers.Add(new SubscriberInfo
                    {
                        Address = delegateObj.Address,
                        Type = StringConstants.StaticMethodSubscriber,
                        MethodName = methodName
                    });
                }
            }
        }

        /// <summary>
        /// Reads _methodPtr from a delegate object and resolves it to a method signature
        /// via <see cref="ClrRuntime.GetMethodByInstructionPointer"/>.
        /// Returns null when the pointer is zero or the method cannot be resolved.
        /// </summary>
        private static string? ResolveMethodName(ClrRuntime runtime, ClrObject delegateObj)
        {
            if (delegateObj.Type == null) return null;
            try
            {
                // Try common delegate fields in order: _methodPtr, _methodPtrAux
                ClrType? cur = delegateObj.Type;
                ClrInstanceField? fPtr = null;
                while (cur != null && fPtr == null)
                {
                    fPtr = cur.GetFieldByName("_methodPtr");
                    cur = cur.BaseType;
                }

                if (fPtr != null)
                {
                    ulong ptr = (ulong)fPtr.Read<IntPtr>(delegateObj, interior: false);
                    if (ptr != 0)
                    {
                        var m = runtime.GetMethodByInstructionPointer(ptr);
                        if (m != null)
                        {
                            string sig = m.Signature ?? m.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(sig)) return sig;
                        }
                    }
                }

                // Try _methodPtrAux as a fallback
                cur = delegateObj.Type;
                ClrInstanceField? fAux = null;
                while (cur != null && fAux == null)
                {
                    fAux = cur.GetFieldByName("_methodPtrAux");
                    cur = cur.BaseType;
                }

                if (fAux != null)
                {
                    ulong ptrAux = (ulong)fAux.Read<IntPtr>(delegateObj, interior: false);
                    if (ptrAux != 0)
                    {
                        var m2 = runtime.GetMethodByInstructionPointer(ptrAux);
                        if (m2 != null)
                        {
                            string sig = m2.Signature ?? m2.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(sig)) return sig;
                        }
                    }
                }

                // Last resort: inspect _methodBase (MethodBase/RuntimeMethodHandle) for a pointer-like field
                cur = delegateObj.Type;
                ClrInstanceField? fBase = null;
                while (cur != null && fBase == null)
                {
                    fBase = cur.GetFieldByName("_methodBase");
                    cur = cur.BaseType;
                }

                if (fBase != null)
                {
                    ClrObject mb = fBase.ReadObject(delegateObj, interior: false);
                    if (mb.IsValid && mb.Type != null)
                    {
                        // Heuristic: look for any native-int sized instance field on the MethodBase wrapper
                        foreach (var field in mb.Type.Fields)
                        {
                            try
                            {
                                if (field.ElementType == ClrElementType.NativeInt || field.ElementType == ClrElementType.Int64 || field.ElementType == ClrElementType.UInt64)
                                {
                                    ulong val = 0;
                                    try { val = (ulong)field.Read<IntPtr>(mb, interior: false); }
                                    catch { continue; }
                                    if (val == 0) continue;
                                    var m3 = runtime.GetMethodByInstructionPointer(val);
                                    if (m3 != null)
                                    {
                                        string sig = m3.Signature ?? m3.Name ?? string.Empty;
                                        if (!string.IsNullOrEmpty(sig)) return sig;
                                    }
                                }
                            }
                            catch { /* ignore field read errors */ }
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLikelySubscriber(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            // Drop compiler-generated closures — they have no stable type identity
            // and are already counted via their enclosing instance's _target.
            if (TypeFilterHelper.IsCompilerGenerated(typeName)) return false;
            if (typeName.Contains("<>", StringComparison.Ordinal)) return false;
            if (typeName.Contains("DisplayClass", StringComparison.Ordinal)) return false;
            // Do NOT filter System.* — framework types (System.Windows.Forms.*,
            // System.Web.*, System.ComponentModel.*, etc.) are legitimate subscribers
            // that retain memory. Dropping them produces artificially low counts.
            return true;
        }

        // ── Heuristic helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Counts the number of <em>extra</em> occurrences of any subscriber address beyond the
        /// first. A count > 0 means the same subscriber object was registered more than once.
        /// </summary>
        private static int CountDuplicateSubscriptions(List<SubscriberInfo> subscribers)
        {
            if (subscribers.Count <= 1) return 0;
            var seen = new Dictionary<ulong, int>(subscribers.Count);
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr == 0) continue;
                seen.TryGetValue(addr, out int cnt);
                seen[addr] = cnt + 1;
            }
            int duplicates = 0;
            foreach (int cnt in seen.Values)
                if (cnt > 1) duplicates += cnt - 1;
            return duplicates;
        }

        /// <summary>
        /// Returns true when at least one subscriber's type implements <see cref="IDisposable"/> —
        /// a strong signal that the subscriber was meant to be torn down (and unsubscribed) but
        /// is still being kept alive by the publisher's delegate chain. Disposable-ness is
        /// resolved once per unique MethodTable and cached by the caller for the lifetime of the
        /// <c>Analyze</c> call (design §9).
        /// </summary>
        private static bool HasDisposableSubscriber(ClrHeap heap, List<SubscriberInfo> subscribers, Dictionary<ulong, bool> disposableTypeCache)
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong mt = subscribers[i].MethodTable;
                if (mt == 0) continue;

                if (!disposableTypeCache.TryGetValue(mt, out bool isDisposable))
                {
                    isDisposable = false;
                    try
                    {
                        ClrType? type = heap.GetTypeByMethodTable(mt);
                        if (type != null)
                        {
                            foreach (ClrInterface iface in type.EnumerateInterfaces())
                            {
                                if (string.Equals(iface.Name, "System.IDisposable", StringComparison.Ordinal))
                                {
                                    isDisposable = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { isDisposable = false; }
                    disposableTypeCache[mt] = isDisposable;
                }

                if (isDisposable) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when a publisher is retaining predominantly younger subscribers —
        /// i.e. publisherGeneration == 2 and many subscribers are Gen0/Gen1. Probes every
        /// subscriber (§9.19) — each generation lookup is an O(1) segment lookup, cheap
        /// regardless of scale, so no cap was needed.
        /// </summary>
        internal static bool CheckLifetimeMismatch(ClrHeap heap, int publisherGeneration, List<SubscriberInfo> subscribers, EventLeakOptions options)
        {
            if (publisherGeneration != 2) return false;
            if (subscribers.Count == 0) return false;
            int gen01Count = 0;
            int probed = 0;
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr == 0 || subscribers[i].Type == StringConstants.StaticMethodSubscriber) continue;
                int gen = SegmentKindMapper.ResolveGeneration(heap,addr);
                if (gen == 0 || gen == 1) gen01Count++;
                probed++;
            }
            if (probed == 0) return false;
            return (double)gen01Count / probed >= options.LifetimeMismatchGen01Threshold;
        }

        public void Dispose() { }
    }
}


