using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class LeakCandidateAnalyzer : IAnalyzer
{
    public string Name => "Leak Candidate Analysis";
    public string Category => "Memory";
    public int Order => 100;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Runtime, context.Cache, context.Progress, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap heap,
        ClrRuntime runtime,
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
        Dictionary<string, int> pinnedTargetCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> dependentTargetCounts = new(StringComparer.Ordinal);
        Dictionary<ulong, string> methodTableNameCache = new(capacity: 128);

        int scannedHandles = 0;
        foreach (ClrHandle handle in runtime.EnumerateHandles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedHandles++;

            string kind = handle.HandleKind.ToString();
            ulong targetAddress = GetTargetAddress(handle);
            if (targetAddress == 0)
                continue;

            string? typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
            if (typeName is null)
                continue;

            if (kind.Contains("Pinned", StringComparison.OrdinalIgnoreCase))
                Increment(pinnedTargetCounts, typeName);
            else if (kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                Increment(dependentTargetCounts, typeName);
        }

        progress?.Report(new(scannedHandles, "building leak candidates", $"{scannedHandles:N0} handles scanned"));

        var candidates = new List<LeakCandidateRecord>(Math.Min(typeStats.Count, 128));
        Dictionary<LeakClass, int> byClass = new();

        foreach ((string typeName, CachedTypeStatistics stat) in typeStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong sampleAddress = cache.GetSampleInstanceAddress(typeName) ?? 0;
            if (sampleAddress == 0)
                continue;

            ClrObject sample = heap.GetObject(sampleAddress);
            if (!sample.IsValid || sample.Type is null)
                continue;

            ulong methodTable = sample.Type.MethodTable;
            if (methodTable == 0 || !aggregates.TryGetValue(methodTable, out TypeAggregateIndexEntry aggregate))
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
                pinnedTargetCounts,
                dependentTargetCounts,
                isFinalizable,
                isDelegate,
                isContainer,
                gen2Pct);

            int score = Score(
                aggregate,
                gen2Pct,
                isFinalizable,
                staticRoots.Contains(sampleAddress),
                pinnedTargetCounts.ContainsKey(typeName),
                dependentTargetCounts.ContainsKey(typeName),
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
        Dictionary<string, int> pinnedTargetCounts,
        Dictionary<string, int> dependentTargetCounts,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (dependentTargetCounts.ContainsKey(typeName))
            return LeakClass.DependentHandleLeak;

        if (pinnedTargetCounts.ContainsKey(typeName))
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

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out int value))
            counts[key] = value + 1;
        else
            counts[key] = 1;
    }

    private static ulong GetTargetAddress(ClrHandle handle)
    {
        object boxedTarget = handle.Object;
        if (boxedTarget is ClrObject clrObject)
            return clrObject.IsValid ? clrObject.Address : 0;

        if (boxedTarget is ulong address)
            return address;

        return 0;
    }

    private static string? ResolveTargetTypeName(ClrHeap heap, ulong targetAddress, Dictionary<ulong, string> methodTableNameCache)
    {
        if (targetAddress == 0)
            return null;

        ClrObject targetObject = heap.GetObject(targetAddress);
        if (!targetObject.IsValid)
            return null;

        ulong methodTable = targetObject.Type?.MethodTable ?? 0;
        if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
            return cached;

        string name = targetObject.Type?.Name ?? StringConstants.UnknownType;
        if (methodTable != 0)
            methodTableNameCache[methodTable] = name;

        return name;
    }
}