using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class LeakCandidateAnalyzer : IDeferredAnalyzer
{
    public string Name => "Leak Candidate Analysis";
    public string Category => "Memory";
    public int Order => 100;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.CompletedRunResults, context.Cache, context.Progress, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IReadOnlyList<AnalyzerRunResult>? completedRunResults,
        IHeapAnalysisCache cache,
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
                gen2Pct);

            int score = Score(
                aggregate,
                gen2Pct,
                isFinalizable,
                staticRoots.Contains(sampleAddress),
                pinnedTargetTypes.Contains(typeName),
                dependentTargetTypes.Contains(typeName),
                isContainer,
                referenceFieldRatio);
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

        int topCount = Math.Min(30, candidates.Count);
        var topCandidates = new List<LeakCandidateRecord>(topCount);
        for (int i = 0; i < topCount; i++)
            topCandidates.Add(candidates[i]);

        return new LeakCandidateDomainResult(candidates.Count, topCandidates, byClass, true);
    }

    private static LeakClass Classify(
        string typeName,
        ulong sampleAddress,
        HashSet<ulong> staticRoots,
        HashSet<string> pinnedTargetTypes,
        HashSet<string> dependentTargetTypes,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (dependentTargetTypes.Contains(typeName))
            return LeakClass.DependentHandleLeak;

        if (pinnedTargetTypes.Contains(typeName))
            return LeakClass.GCHandleRetention;

        if (staticRoots.Contains(sampleAddress))
            return LeakClass.StaticRetention;

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
        double referenceFieldRatio)
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