using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;

namespace DumpDetective.Analysis.Analyzers
{
    internal sealed class MemoryAnalyzer : IAnalyzer
    {
        public string Name => "Memory Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryAnalysisOptions options = context.GetOption<MemoryAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, new MemoryAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, MemoryAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "building memory snapshot"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            // Read Phase 1 GlobalSizeBuckets if available (zero extra heap scan)
            long[]? globalBuckets = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                globalBuckets = heapIndex.GlobalSizeBuckets;

            return BuildDomainResult(heap, cache, typeStats, globalBuckets, options);
        }

        private static MemoryDomainResult BuildDomainResult(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            Dictionary<string, CachedTypeStatistics> typeStats,
            long[]? globalSizeBuckets,
            MemoryAnalysisOptions options)
        {
            ulong totalMemory = 0;
            ulong totalLohMemory = 0;
            int totalObjects = 0;
            int lohObjects = 0;
            foreach (var stat in typeStats.Values)
            {
                totalMemory += stat.TotalSize;
                totalLohMemory += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;
            }

            double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;

            var bySize = new List<CachedTypeStatistics>(typeStats.Values);
            bySize.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            var byCount = new List<CachedTypeStatistics>(typeStats.Values);
            byCount.Sort((a, b) => b.Count.CompareTo(a.Count));

                static TypeSnapshot ToSnapshot(CachedTypeStatistics s, ulong retainedBytes, ulong sampleAddress)
            {
                ulong avgSize = s.Count > 0 ? s.TotalSize / (ulong)s.Count : 0;
                return new TypeSnapshot(s.TypeName, s.Count, s.TotalSize, s.LohSize,
                    AverageSize: avgSize,
                    EstimatedRetainedBytes: retainedBytes,
                    SampleAddress: sampleAddress,
                    ModuleName: string.IsNullOrWhiteSpace(s.ModuleName) ? null : s.ModuleName);
            }

            static ulong EstimateRetained(ClrHeap heap, IHeapAnalysisCache cache, string typeName, HashSet<ulong> claimedAddresses)
            {
                ulong sampleAddress = cache.GetSampleInstanceAddress(typeName) ?? 0;
                if (sampleAddress == 0)
                    return 0;

                try
                {
                    ClrObject root = heap.GetObject(sampleAddress);
                    return BoundedRetainedSizeBfs.ComputeExclusiveRetained(root, heap, claimedAddresses);
                }
                catch
                {
                    return 0;
                }
            }
            // Build size-bucket histogram from Phase 1 counters (zero heap scan)
            IReadOnlyList<SizeBucketEntry>? histogram = null;
            if (globalSizeBuckets is { Length: >= SizeBucketHelper.BucketCount })
            {
                var entries = new SizeBucketEntry[SizeBucketHelper.BucketCount];
                // Compute per-bucket total bytes from typeStats for each bucket boundary
                // GlobalSizeBuckets only stores object counts; derive TotalBytes from typeStats
                // by distributing each type's average size into its bucket.
                var bucketBytes = new ulong[SizeBucketHelper.BucketCount];
                foreach (var stat in typeStats.Values)
                {
                    if (stat.Count <= 0) continue;
                    ulong avgSize = stat.TotalSize / (ulong)stat.Count;
                    int idx = SizeBucketHelper.GetBucketIndex(avgSize);
                    bucketBytes[idx] += stat.TotalSize;
                }

                for (int i = 0; i < SizeBucketHelper.BucketCount; i++)
                {
                    entries[i] = new SizeBucketEntry(
                        RangeLabel: SizeBucketHelper.BucketLabels[i],
                        ObjectCount: globalSizeBuckets[i],
                        TotalBytes: bucketBytes[i]);
                }
                histogram = entries;
            }

            int topN = Math.Min(options.TopBySizeCount, bySize.Count);
            var topBySize = new List<TypeSnapshot>(topN);
            HashSet<ulong> retainedClaims = new();
            for (int i = 0; i < topN; i++)
            {
                CachedTypeStatistics stat = bySize[i];
                topBySize.Add(ToSnapshot(stat, EstimateRetained(heap, cache, stat.TypeName, retainedClaims), cache.GetSampleInstanceAddress(stat.TypeName) ?? 0));
            }

            int topM = Math.Min(options.TopByCountCount, byCount.Count);
            var topByCount = new List<TypeSnapshot>(topM);
            HashSet<ulong> retainedCountClaims = new();
            for (int i = 0; i < topM; i++)
            {
                CachedTypeStatistics stat = byCount[i];
                topByCount.Add(ToSnapshot(stat, EstimateRetained(heap, cache, stat.TypeName, retainedCountClaims), cache.GetSampleInstanceAddress(stat.TypeName) ?? 0));
            }

            return new MemoryDomainResult(
                totalMemory,
                totalLohMemory,
                lohPct,
                totalObjects,
                lohObjects,
                options.LohThresholdBytes,
                typeStats.Count,
                topBySize,
                topByCount,
                SizeBucketHistogram: histogram);
        }

        public void Dispose() { }
    }
}
