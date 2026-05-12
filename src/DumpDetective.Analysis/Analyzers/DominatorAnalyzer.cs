using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class DominatorAnalyzer : IAnalyzer
{
    public string Name => "Dominator Analysis";
    public string Category => "Memory";
    public int Order => 110;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RetentionOptions options = context.AnalysisOptions.MemoryLeak;
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, cancellationToken).Stamp(this));
    }

    private static DominatorDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CachedTypeStatistics> typeStats = cache.GetOrBuildTypeStatistics(heap);
        if (typeStats.Count == 0)
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>());

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? aggregates = null;
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
            aggregates = heapIndex.TypeAggregates;

        var candidates = new List<(string TypeName, ulong SampleAddress, int Count, ulong TotalSize, ulong LohSize, int Gen2Count, ulong Score)>(capacity: Math.Min(32, typeStats.Count));

        foreach (KeyValuePair<string, CachedTypeStatistics> kv in typeStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong sampleAddress = cache.GetSampleInstanceAddress(kv.Key) ?? 0;
            if (sampleAddress == 0)
                continue;

            ulong totalSize = kv.Value.TotalSize;
            ulong lohSize = kv.Value.LohSize;
            int count = kv.Value.Count;
            int gen2Count = 0;

            if (aggregates is not null)
            {
                ClrObject sample = heap.GetObject(sampleAddress);
                if (sample.IsValid && sample.Type is not null && aggregates.TryGetValue(sample.Type.MethodTable, out TypeAggregateIndexEntry aggregate))
                {
                    gen2Count = aggregate.Gen2Count;
                    totalSize = aggregate.TotalSize;
                    lohSize = aggregate.LohSize;
                    count = (int)Math.Min(int.MaxValue, aggregate.Count);
                }
            }

            ulong averageSize = count > 0 ? Math.Max(1UL, totalSize / (ulong)count) : 1;
            ulong score = totalSize + lohSize + (ulong)Math.Max(0, gen2Count) * averageSize;
            if (count >= 1_000)
                score += totalSize / 4;

            candidates.Add((kv.Key, sampleAddress, count, totalSize, lohSize, gen2Count, score));
        }

        if (candidates.Count == 0)
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>())
            {
                HeuristicOnly = true,
                MaxBreadth = options.MaxLeakScanObjects,
                MaxDepth = 20
            };

        candidates.Sort(static (a, b) =>
        {
            int score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;

            int size = b.TotalSize.CompareTo(a.TotalSize);
            if (size != 0)
                return size;

            return StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        int topCount = Math.Min(options.TopHighlyReferencedObjectsToShow, Math.Min(candidates.Count, 20));
        var topTypes = new List<TypeSnapshot>(topCount);
        ulong totalEstimatedRetainedBytes = 0;

        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;
        const int MaxDepth = 20;

        for (int i = 0; i < topCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string typeName, ulong sampleAddress, int count, ulong totalSize, ulong lohSize, _, _) = candidates[i];
            ClrObject root = heap.GetObject(sampleAddress);
            if (!root.IsValid || root.Type is null)
                continue;

            ulong retainedBytes = BoundedRetainedSizeBfs.ComputeExclusiveRetained(root, heap, new HashSet<ulong>(capacity: 256), maxBreadth, MaxDepth);
            totalEstimatedRetainedBytes += retainedBytes;

            ulong averageSize = count > 0 ? totalSize / (ulong)count : 0;
            topTypes.Add(new TypeSnapshot(
                typeName,
                count,
                totalSize,
                lohSize,
                AverageSize: averageSize,
                EstimatedRetainedBytes: retainedBytes,
                SampleAddress: sampleAddress));
        }

        topTypes.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        return new DominatorDomainResult(
            candidates.Count,
            topTypes.Count,
            totalEstimatedRetainedBytes,
            topTypes,
            HeuristicOnly: true,
            MaxBreadth: maxBreadth,
            MaxDepth: MaxDepth);
    }
}