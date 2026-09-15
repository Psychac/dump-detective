using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Cross-references per-type heap facts against three other analyzers' already-completed results
/// (pinned/dependent-handle targets, timer counts, direct GC roots) to score and classify leak
/// candidates. Unlike every other analyzer in this retyping wave, this one's defining behavior is
/// cross-*analyzer* correlation, not cross-*object* graph traversal — it detects nothing itself,
/// it re-ranks what others already found.
/// </summary>
/// <remarks>
/// Tier-1-only, same re-classification as `DominatorAnalyzer`/`ReferenceChainAnalyzer`'s own
/// batches — every ClrMD touch point here is coarse/structural.
/// <para>
/// <b>The cross-analyzer dependency</b> (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md)
/// is why this was saved for last: <c>Sdk.Analysis.AnalysisContext</c> has no slot for "other
/// analyzers' finished results" — deliberately, since <c>AnalyzerRunResult</c>/<c>AnalyzerDomainResult</c>
/// live in <c>Core.Models</c>, which the SDK cannot reference. The real, principled fix
/// (docs/refactor/modularity/phase-4-session-orchestration.md's dependency-graph orchestrator,
/// where nodes declare <c>Produces</c>/<c>Consumes</c> instead of a single "run me last" bucket) is
/// Phase 4 and hasn't started. This class still implements <c>IDeferredAnalyzer</c> and still reads
/// <c>context.CompletedRunResults</c> in <see cref="ICustomizesAnalysisContext.BuildAnalysisContext"/>,
/// extracting only the three specific signals actually consumed — <see cref="Core.Models.IHandleRetentionSignal"/>'s
/// pinned/dependent target types, <see cref="Core.Models.ILogicalTimerCountSignal"/>'s logical timer
/// count, <see cref="Core.Models.IDirectRootSignal"/>'s direct-root lookup — into
/// <see cref="LeakCandidateAnalyzerOptions"/>, handed through the existing <c>AnalyzerOptions</c>
/// slot. Reading through those three structural interfaces (docs/refactor/modularity/phase-3-plugin-packaging.md)
/// rather than the concrete <c>GCHandleDomainResult</c>/<c>GCRootDomainResult</c>/<c>TimerLeakDomainResult</c>
/// types means this analyzer has no compile-time dependency on whichever package ends up owning
/// them — it's what let all three move into per-domain plugin packages without this one needing to
/// reference each of those packages in turn. The inner analyzer (<see cref="AnalyzeAsync"/>) still
/// has zero knowledge that <c>CompletedRunResults</c> exists — it just reads plain fields off
/// <see cref="LeakCandidateAnalyzerOptions"/>. When Phase 4 lands, this context-building-side
/// extraction is replaced by real graph edges, not reworked from scratch.
/// </para>
/// <para>
/// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> replaces the pre-retyping analyzer's own
/// raw <c>TypeAggregateIndexEntry</c>/<c>TypeShapeEntry</c> dictionary reads — <c>TotalFields</c>/
/// <c>ReferenceFields</c> (from <c>ObjectShapeAnalyzer</c>'s batch) cover the reference-field-ratio
/// signal, and <c>IsDelegateType</c> (new — see its own remarks) covers the one
/// <c>TypeAggregateFlags</c> bit no prior batch had a consumer for yet.
/// <see cref="IHeapRootQuery.EnumerateRoots"/> replaces <c>cache.GetStaticRootedAddresses</c> — a
/// filter over already-resolved roots. <see cref="IHeapRootPathQuery.TryFindRootPath"/> replaces
/// the pre-retyping analyzer's own hand-built <c>RootPathFinder</c> for the root-chain fallback
/// search — its output shape (<c>"RootKind: Type@0xAddr -&gt; ... -&gt; Type@0xAddr"</c>) already
/// matches this analyzer's own <c>FormatChain</c> exactly, so no raw-address overload is needed here
/// the way <c>ReferenceChainAnalyzer</c> needed one.
/// </para>
/// <para>
/// <b>Dropped, not ported</b>: the force-expand predicate this analyzer's own pre-retyping
/// <c>BuildRootPathFinder</c> wired through <c>ReferenceChainOptions.KnownLeakTypePatterns</c> — same
/// already-documented hang risk <see cref="IHeapRootPathQuery"/>'s own remarks describe, re-confirmed
/// (not re-attempted) a third time now. Internal-only telemetry counters dropped too, same as
/// `ReferenceChainAnalyzer`'s batch — never surfaced in <see cref="LeakCandidateDomainResult"/>.
/// </para>
/// </remarks>
[AnalyzerModule(key: "leak-candidate", displayName: "Leak Candidate Analysis", order: 210, tags: ["leaks"])]
public sealed class LeakCandidateAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult, IDeferredAnalyzer, ICustomizesAnalysisContext
{
    public string Name => "Leak Candidate Analysis";
    public string Category => "Memory";
    public int Order => 100;

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    // Reads context.CompletedRunResults for cross-analyzer correlation — see this class's own
    // remarks on why this lives here rather than in a real dependency-graph orchestrator (Phase 4).
    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context)
    {
        IReadOnlyList<AnalyzerRunResult>? completedRunResults = context.CompletedRunResults;

        // Sourced from the already-completed gc-handle analyzer run rather than re-walking
        // runtime.EnumerateHandles() here — avoids a second full handle scan for the same signal.
        // Read via the structural signal interface, not the concrete DomainResult type, so this
        // analyzer has no compile-time dependency on whichever package owns GCHandleDomainResult.
        IHandleRetentionSignal? handleRetentionSignal = completedRunResults?.GetResult<IHandleRetentionSignal>();
        var pinnedTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        var dependentTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        if (handleRetentionSignal is not null)
        {
            foreach (NameCountEntry entry in handleRetentionSignal.TopPinnedTargetTypes)
                pinnedTargetTypes.Add(entry.Name);
            foreach (NameCountEntry entry in handleRetentionSignal.DependentTopTargetTypes)
                dependentTargetTypes.Add(entry.Name);
        }

        // Sourced from the already-completed timer-leak analyzer run — LogicalTimerCount is its
        // de-duplicated count (TimerQueueTimerCount), not the raw double-counted TotalTimers, so
        // it's the right magnitude signal to correlate against each timer wrapper type's row here.
        ILogicalTimerCountSignal? logicalTimerCountSignal = completedRunResults?.GetResult<ILogicalTimerCountSignal>();
        int logicalTimerCount = logicalTimerCountSignal?.LogicalTimerCount ?? 0;

        // Sourced from the already-completed GC-root analyzer run — a cheap, exact shortcut for
        // candidates that are themselves a direct root target, before falling back to a bounded
        // root-path search inside the inner analyzer.
        IDirectRootSignal? directRootSignal = completedRunResults?.GetResult<IDirectRootSignal>();
        Dictionary<ulong, DirectRootEntry>? directRootsByTargetAddress = null;
        if (directRootSignal is not null && directRootSignal.TopRootsBySeverity.Count > 0)
        {
            directRootsByTargetAddress = new Dictionary<ulong, DirectRootEntry>(directRootSignal.TopRootsBySeverity.Count);
            foreach (DirectRootEntry root in directRootSignal.TopRootsBySeverity)
                directRootsByTargetAddress[root.TargetAddress] = root;
        }

        var options = new LeakCandidateAnalyzerOptions(
            new ReferenceChainOptions(), pinnedTargetTypes, dependentTargetTypes, logicalTimerCount, directRootsByTargetAddress);

        return AnalysisContextBuilder.Build(context, options);
    }

    // Bounds root-chain enrichment (P3-1/P3-2), not the returned candidate population — matches
    // GCRootAnalyzer.StackOwnerAttributionLimit's precedent: purely cosmetic per-candidate lookup
    // that's too costly to run for every candidate, so scoped to the highest-suspicion ones only.
    private const int RootChainTopN = 20;

    // Same Warning/Critical thresholds TimerLeakFindingGenerator uses, so a candidate only earns
    // the TimerLeak classification/score boost once the timer analyzer itself considers the count
    // leak-worthy.
    private const int TimerLeakWarningThreshold = 100;
    private const int TimerLeakCriticalThreshold = 250;

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapRootQuery heapRoots = context.HeapRoots
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRoots}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");

        var options = context.AnalyzerOptions as LeakCandidateAnalyzerOptions ?? LeakCandidateAnalyzerOptions.Empty;

        LastResult = Analyze(typeStatistics, heapRoots, objectLookup, rootPaths, options, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static LeakCandidateDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IHeapRootQuery heapRoots,
        IHeapObjectLookup objectLookup,
        IHeapRootPathQuery rootPaths,
        LeakCandidateAnalyzerOptions options,
        CancellationToken cancellationToken)
    {
        // This analyzer needs the exact per-type index (Gen2 counts, type shape data) and never had
        // a live-heap fallback pre-retyping — HasExactGenerationData is the same "is a disk index
        // present" gate the old TryGetHeapIndex check was.
        if (!typeStatistics.HasExactGenerationData)
            return new LeakCandidateDomainResult(0, [], new Dictionary<LeakClass, int>(), true);

        IReadOnlyDictionary<string, HeapTypeStatistics> typeStats = typeStatistics.GetTypeStatistics();
        if (typeStats.Count == 0)
            return new LeakCandidateDomainResult(0, [], new Dictionary<LeakClass, int>(), true);

        var staticRoots = new HashSet<ulong>();
        foreach (HeapRootRef root in heapRoots.EnumerateRoots())
        {
            if (root.Kind is HeapRootKind.StaticVar or HeapRootKind.ThreadStaticVar)
                staticRoots.Add(root.TargetAddress);
        }

        var candidates = new List<LeakCandidateRecord>(Math.Min(typeStats.Count, 128));
        var byClass = new Dictionary<LeakClass, int>();
        var sampleAddressByTypeName = new Dictionary<string, ulong>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string typeName = kv.Key;
            HeapTypeStatistics stat = kv.Value;
            if (stat.SampleAddress == 0)
                continue;

            sampleAddressByTypeName[typeName] = stat.SampleAddress;

            double gen2Pct = stat.InstanceCount > 0 ? stat.Gen2Count * 100.0 / stat.InstanceCount : 0.0;
            bool isFinalizable = stat.IsFinalizableType;
            bool isContainer = stat.IsArray
                || typeName.Contains("Dictionary", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("ConcurrentDictionary", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("List<", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("Queue", StringComparison.OrdinalIgnoreCase);

            double referenceFieldRatio = stat.TotalFields > 0
                ? stat.ReferenceFields / (double)stat.TotalFields
                : 0.0;

            bool isStaticRooted = staticRoots.Contains(stat.SampleAddress);
            bool isPinned = options.PinnedTargetTypes.Contains(typeName);
            bool isDependent = options.DependentTargetTypes.Contains(typeName);

            LeakClass classification = Classify(
                typeName,
                isStaticRooted,
                isPinned,
                isDependent,
                isFinalizable,
                stat.IsDelegateType,
                isContainer,
                gen2Pct,
                options.LogicalTimerCount);

            int score = Score(
                stat.TotalSize,
                stat.Gen2Count,
                gen2Pct,
                isFinalizable,
                isStaticRooted,
                isPinned,
                isDependent,
                isContainer,
                referenceFieldRatio,
                typeName,
                stat.IsDelegateType,
                options.LogicalTimerCount);

            FindingSeverity severity = GetSeverity(score);

            string? rootKind = classification switch
            {
                LeakClass.StaticRetention => "StaticRoot",
                LeakClass.GCHandleRetention => "GCHandle",
                LeakClass.DependentHandleLeak => "DependentHandle",
                LeakClass.FinalizerRetention => "FinalizerQueue",
                LeakClass.EventLeak => "EventLeak",
                LeakClass.CacheLeak => "Cache",
                LeakClass.ThreadLocalLeak => "ThreadLocal",
                LeakClass.TimerLeak => "Timer",
                _ => null
            };

            candidates.Add(new LeakCandidateRecord(
                TypeName: typeName,
                TotalSize: stat.TotalSize,
                InstanceCount: stat.InstanceCount,
                Gen2Pct: gen2Pct,
                SuspicionScore: score,
                Severity: severity,
                Classification: classification,
                RootKind: rootKind,
                IsFinalizable: isFinalizable,
                IsContainer: isContainer,
                ReferenceFieldRatio: referenceFieldRatio));

            Increment(byClass, classification);
        }

        candidates.Sort(static (a, b) =>
        {
            int score = b.SuspicionScore.CompareTo(a.SuspicionScore);
            if (score != 0)
                return score;

            int size = b.TotalSize.CompareTo(a.TotalSize);
            if (size != 0)
                return size;

            return StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        EnrichTopCandidatesWithRootChains(candidates, objectLookup, rootPaths, sampleAddressByTypeName, options, cancellationToken);

        // Complete ranked population, no Top-N cap (§11.2 D5) — the render layer paginates.
        return new LeakCandidateDomainResult(candidates.Count, candidates, byClass, true);
    }

    // P3-1/P3-2 (docs/analysis/phase1/gcroot-analyzer-audit.md): genuine root chains for the
    // top-scored candidates, cross-referencing GCRootAnalyzer's already-recorded direct root
    // targets first (cheap, exact) and falling back to IHeapRootPathQuery's bounded, reverse-index-
    // backed search for candidates that aren't themselves a direct root target but are reachable
    // from one.
    private static void EnrichTopCandidatesWithRootChains(
        List<LeakCandidateRecord> candidates,
        IHeapObjectLookup objectLookup,
        IHeapRootPathQuery rootPaths,
        Dictionary<string, ulong> sampleAddressByTypeName,
        LeakCandidateAnalyzerOptions options,
        CancellationToken cancellationToken)
    {
        int topN = Math.Min(candidates.Count, RootChainTopN);
        if (topN == 0)
            return;

        var searchOptions = new RootPathSearchOptions(
            options.ReferenceChainOptions.MaxCandidateNodes, options.ReferenceChainOptions.MaxCandidateDepth,
            options.ReferenceChainOptions.MaxRootExpansionDepth, options.ReferenceChainOptions.LargeFanoutThreshold);

        for (int i = 0; i < topN; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeakCandidateRecord candidate = candidates[i];

            if (!sampleAddressByTypeName.TryGetValue(candidate.TypeName, out ulong sampleAddress))
                continue;

            if (options.DirectRootsByTargetAddress is not null
                && options.DirectRootsByTargetAddress.TryGetValue(sampleAddress, out DirectRootEntry? directRoot))
            {
                string fieldSuffix = directRoot.FieldDescription is not null ? $" {directRoot.FieldDescription}" : string.Empty;
                candidates[i] = candidate with
                {
                    RootChain = $"{directRoot.RootKind}{fieldSuffix} -> {FormatNodeByAddress(objectLookup, sampleAddress)}"
                };
                continue;
            }

            if (rootPaths.TryFindRootPath(sampleAddress, searchOptions, out string? formattedPath, out _) && formattedPath is not null)
                candidates[i] = candidate with { RootChain = formattedPath };
        }
    }

    private static string FormatNodeByAddress(IHeapObjectLookup objectLookup, ulong address)
    {
        string typeName = objectLookup.TryGetObject(address, out HeapObjectRef obj) ? obj.TypeDisplayName : "<invalid>";
        return $"{typeName}@0x{address:X}";
    }

    private static LeakClass Classify(
        string typeName,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct,
        int logicalTimerCount)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (isDependent)
            return LeakClass.DependentHandleLeak;

        if (isPinned)
            return LeakClass.GCHandleRetention;

        if (isStaticRooted)
            return LeakClass.StaticRetention;

        if (logicalTimerCount >= TimerLeakWarningThreshold && TimerWrapperTypeNames.Names.Contains(typeName))
            return LeakClass.TimerLeak;

        if (isDelegate || typeName.Contains("Event", StringComparison.OrdinalIgnoreCase))
            return LeakClass.EventLeak;

        if (isFinalizable && gen2Pct > 50.0)
            return LeakClass.FinalizerRetention;

        if (isContainer && gen2Pct > 70.0)
            return LeakClass.CacheLeak;

        return LeakClass.Unknown;
    }

    private static int Score(
        ulong totalSize,
        long gen2Count,
        double gen2Pct,
        bool isFinalizable,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isContainer,
        double referenceFieldRatio,
        string typeName,
        bool isDelegate,
        int logicalTimerCount)
    {
        int score = 0;

        if (gen2Pct > 80.0)
            score += 30;
        if (totalSize > 100UL * 1024 * 1024)
            score += 20;
        if (isFinalizable && gen2Count > 1000)
            score += 15;
        if (isStaticRooted)
            score += 10;
        if (isPinned)
            score += 10;
        if (isDependent)
            score += 10;
        if (isContainer)
            score += 5;
        if (referenceFieldRatio > 0.5)
            score += 5;
        if (isDelegate)
            score += 5;
        if (TimerWrapperTypeNames.Names.Contains(typeName))
        {
            if (logicalTimerCount >= TimerLeakCriticalThreshold)
                score += 20;
            else if (logicalTimerCount >= TimerLeakWarningThreshold)
                score += 10;
        }

        return score;
    }

    private static FindingSeverity GetSeverity(int score)
        => score >= 90
            ? FindingSeverity.Critical
            : score >= 70
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

    private static void Increment(Dictionary<LeakClass, int> counts, LeakClass classification)
    {
        if (counts.TryGetValue(classification, out int value))
            counts[classification] = value + 1;
        else
            counts[classification] = 1;
    }
}
