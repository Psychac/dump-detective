using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class LeakCandidateAnalyzer : IDeferredAnalyzer
{
    // Bounds root-chain enrichment (P3-1/P3-2), not the returned candidate population — matches
    // GCRootAnalyzer.StackOwnerAttributionLimit's precedent: purely cosmetic per-candidate lookup
    // that's too costly to run for every candidate, so scoped to the highest-suspicion ones only.
    private const int RootChainTopN = 20;

    public string Name => "Leak Candidate Analysis";
    public string Category => "Memory";
    public int Order => 100;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.CompletedRunResults, context.Cache, context.AnalysisOptions.ReferenceChain, context.Progress, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IReadOnlyList<AnalyzerRunResult>? completedRunResults,
        IHeapAnalysisCache cache,
        ReferenceChainOptions referenceChainOptions,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (cache is not HeapAnalysisCache heapCache || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex) || heapIndex is null)
        {
            return new LeakCandidateDomainResult(0, [], new Dictionary<LeakClass, int>(), true);
        }

        Dictionary<string, CachedTypeStatistics> typeStats = cache.GetOrBuildTypeStatistics(heap);
        if (typeStats.Count == 0)
            return new LeakCandidateDomainResult(0, [], new Dictionary<LeakClass, int>(), true);

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = heapIndex.TypeAggregates;
        IReadOnlyDictionary<ulong, TypeShapeEntry>? shapes = heapIndex.TypeShapeCache;

        HashSet<ulong> staticRoots = cache.GetStaticRootedAddresses(heap);

        // Sourced from the already-completed gc-handle analyzer run rather than re-walking
        // runtime.EnumerateHandles() here — avoids a second full handle scan for the same signal.
        GCHandleDomainResult? gcHandleResult = completedRunResults?.GetResult<GCHandleDomainResult>();
        HashSet<string> pinnedTargetTypes = new(StringComparer.Ordinal);
        HashSet<string> dependentTargetTypes = new(StringComparer.Ordinal);
        if (gcHandleResult is not null)
        {
            foreach (NameCountEntry entry in gcHandleResult.TopPinnedTargetTypes ?? [])
                pinnedTargetTypes.Add(entry.Name);
            foreach (NameCountEntry entry in gcHandleResult.DependentTopTargetTypes ?? [])
                dependentTargetTypes.Add(entry.Name);
        }

        // Sourced from the already-completed timer-leak analyzer run — LogicalTimerCount is its
        // de-duplicated count (TimerQueueTimerCount), not the raw double-counted TotalTimers, so
        // it's the right magnitude signal to correlate against each timer wrapper type's row here.
        TimerLeakDomainResult? timerLeakResult = completedRunResults?.GetResult<TimerLeakDomainResult>();
        int logicalTimerCount = timerLeakResult?.LogicalTimerCount ?? 0;

        var candidates = new List<LeakCandidateRecord>(Math.Min(aggregates.Count, 128));
        Dictionary<LeakClass, int> byClass = new();

        foreach ((ulong methodTable, TypeAggregateIndexEntry aggregate) in aggregates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            if (type?.Name is not string typeName || !typeStats.ContainsKey(typeName))
                continue;

            ulong sampleAddress = aggregate.SampleAddress;
            if (sampleAddress == 0)
                continue;

            TypeShapeEntry shape = shapes is not null && shapes.TryGetValue(methodTable, out TypeShapeEntry foundShape)
                ? foundShape
                : default;

            double gen2Pct = aggregate.Count > 0 ? aggregate.Gen2Count * 100.0 / aggregate.Count : 0.0;
            bool isFinalizable = aggregate.Flags.HasFlag(TypeAggregateFlags.IsFinalizableType);
            bool isArray = aggregate.Flags.HasFlag(TypeAggregateFlags.IsArrayType);
            bool isDelegate = aggregate.Flags.HasFlag(TypeAggregateFlags.IsDelegateType);
            bool isContainer = isArray
                || typeName.Contains("Dictionary", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("ConcurrentDictionary", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("List<", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("Queue", StringComparison.OrdinalIgnoreCase);

            double referenceFieldRatio = shape.TotalFields > 0
                ? shape.RefFields / (double)shape.TotalFields
                : 0.0;

            LeakClass classification = Classify(
                typeName,
                sampleAddress,
                staticRoots,
                pinnedTargetTypes,
                dependentTargetTypes,
                isFinalizable,
                isDelegate,
                isContainer,
                gen2Pct,
                logicalTimerCount);

            int score = Score(
                aggregate,
                gen2Pct,
                isFinalizable,
                staticRoots.Contains(sampleAddress),
                pinnedTargetTypes.Contains(typeName),
                dependentTargetTypes.Contains(typeName),
                isContainer,
                referenceFieldRatio,
                typeName,
                logicalTimerCount);
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
                TotalSize: aggregate.TotalSize,
                InstanceCount: aggregate.Count,
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

        EnrichTopCandidatesWithRootChains(candidates, heap, cache, completedRunResults, referenceChainOptions, cancellationToken);

        // Complete ranked population, no Top-N cap (§11.2 D5) — the render layer paginates.
        return new LeakCandidateDomainResult(candidates.Count, candidates, byClass, true);
    }

    // P3-1/P3-2 (docs/analysis/phase1/gcroot-analyzer-audit.md): genuine root chains for the
    // top-scored candidates, cross-referencing GCRootAnalyzer's already-recorded direct root
    // targets first (cheap, exact) and falling back to a bounded RootPathFinder BFS — the same
    // reverse-index-backed search CollectionAnalyzer/DominatorAnalyzer/EventLeakAnalyzer/
    // ReferenceChainAnalyzer/StaticRootLeakDetector/TimerLeakAnalyzer already use — for candidates
    // that aren't themselves a direct root target but are reachable from one.
    private static void EnrichTopCandidatesWithRootChains(
        List<LeakCandidateRecord> candidates,
        ClrHeap heap,
        IHeapAnalysisCache cache,
        IReadOnlyList<AnalyzerRunResult>? completedRunResults,
        ReferenceChainOptions referenceChainOptions,
        CancellationToken cancellationToken)
    {
        int topN = Math.Min(candidates.Count, RootChainTopN);
        if (topN == 0)
            return;

        GCRootDomainResult? gcRoot = completedRunResults?.GetResult<GCRootDomainResult>();
        Dictionary<ulong, RootFinding>? directRootsByTargetAddress = null;
        if (gcRoot is not null && gcRoot.TopRootsBySeverity.Count > 0)
        {
            directRootsByTargetAddress = new Dictionary<ulong, RootFinding>(gcRoot.TopRootsBySeverity.Count);
            foreach (RootFinding root in gcRoot.TopRootsBySeverity)
                directRootsByTargetAddress[root.TargetAddress] = root;
        }

        RootPathFinder? finder = null;
        IReadOnlyList<(string RootKind, ulong Address)>? roots = null;

        for (int i = 0; i < topN; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeakCandidateRecord candidate = candidates[i];

            ulong sampleAddress = default;
            if (!TryGetSampleAddress(cache, candidate.TypeName, out sampleAddress))
                continue;

            if (directRootsByTargetAddress is not null && directRootsByTargetAddress.TryGetValue(sampleAddress, out RootFinding? directRoot) && directRoot is not null)
            {
                string fieldSuffix = directRoot.FieldDescription is not null ? $" {directRoot.FieldDescription}" : string.Empty;
                candidates[i] = candidate with
                {
                    RootChain = $"{directRoot.RootKind}{fieldSuffix} -> {FormatNodeByAddress(heap, sampleAddress)}"
                };
                continue;
            }

            finder ??= BuildRootPathFinder(heap, cache, referenceChainOptions, out roots);
            if (finder is null || roots is null || roots.Count == 0)
                continue;

            bool found = finder.TryFindAnyRootPath(
                sampleAddress, roots, out string? rootKind, out List<ulong>? path, out _, out _, out _, cancellationToken);

            if (found && rootKind is not null && path is not null)
            {
                candidates[i] = candidate with { RootChain = FormatChain(heap, rootKind, path) };
            }
        }
    }

    private static bool TryGetSampleAddress(IHeapAnalysisCache cache, string typeName, out ulong sampleAddress)
    {
        sampleAddress = cache.GetSampleInstanceAddress(typeName) ?? 0;
        return sampleAddress != 0;
    }

    private static RootPathFinder? BuildRootPathFinder(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        ReferenceChainOptions options,
        out IReadOnlyList<(string RootKind, ulong Address)> roots)
    {
        roots = cache.GetOrBuildValidRoots(heap);
        if (roots.Count == 0)
            return null;

        var limits = new RootPathSearchLimits
        {
            MaxCandidateNodes = options.MaxCandidateNodes,
            MaxCandidateDepth = options.MaxCandidateDepth,
            MaxRootExpansionDepth = options.MaxRootExpansionDepth,
            LargeFanoutThreshold = options.LargeFanoutThreshold,
        };

        var provider = new ReferenceGraph(heap);
        var telemetry = new ReferenceChainAnalyzer.TelemetryCounters();

        return new RootPathFinder(
            heap,
            provider,
            limits,
            telemetry.AsProxy(),
            ReferenceChainAnalyzer.IsNoisyType,
            type => ReferenceChainAnalyzer.IsKnownLeakType(type, options.KnownLeakTypePatterns),
            cache.TryGetReverseIndexProvider(),
            cache);
    }

    private static string FormatChain(ClrHeap heap, string rootKind, IReadOnlyList<ulong> addresses)
    {
        var parts = new List<string>(addresses.Count);
        for (int i = 0; i < addresses.Count; i++)
            parts.Add(FormatNodeByAddress(heap, addresses[i]));

        return $"{rootKind}: {string.Join(" -> ", parts)}";
    }

    private static string FormatNodeByAddress(ClrHeap heap, ulong address)
    {
        ClrObject obj = heap.GetObject(address);
        string typeName = obj.IsValid ? (obj.Type?.Name ?? "?") : "<invalid>";
        return $"{typeName}@0x{address:X}";
    }

    // Same Warning/Critical thresholds TimerLeakFindingGenerator uses, so a candidate only earns
    // the TimerLeak classification/score boost once the timer analyzer itself considers the count
    // leak-worthy.
    private const int TimerLeakWarningThreshold = 100;
    private const int TimerLeakCriticalThreshold = 250;

    private static LeakClass Classify(
        string typeName,
        ulong sampleAddress,
        HashSet<ulong> staticRoots,
        HashSet<string> pinnedTargetTypes,
        HashSet<string> dependentTargetTypes,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct,
        int logicalTimerCount)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (dependentTargetTypes.Contains(typeName))
            return LeakClass.DependentHandleLeak;

        if (pinnedTargetTypes.Contains(typeName))
            return LeakClass.GCHandleRetention;

        if (staticRoots.Contains(sampleAddress))
            return LeakClass.StaticRetention;

        if (logicalTimerCount >= TimerLeakWarningThreshold && TimerLeakAnalyzer.NamedTimerWrapperTypeNames.Contains(typeName))
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
        TypeAggregateIndexEntry aggregate,
        double gen2Pct,
        bool isFinalizable,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isContainer,
        double referenceFieldRatio,
        string typeName,
        int logicalTimerCount)
    {
        int score = 0;

        if (gen2Pct > 80.0)
            score += 30;
        if (aggregate.TotalSize > 100UL * 1024 * 1024)
            score += 20;
        if (isFinalizable && aggregate.Gen2Count > 1000)
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
        if (aggregate.Flags.HasFlag(TypeAggregateFlags.IsDelegateType))
            score += 5;
        if (TimerLeakAnalyzer.NamedTimerWrapperTypeNames.Contains(typeName))
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