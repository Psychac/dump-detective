using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Analyzers
{
    internal class MemoryAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85000;

        public string Name => "Memory Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "building memory snapshot"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            // Read Phase 1 GlobalSizeBuckets if available (zero extra heap scan)
            long[]? globalBuckets = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                globalBuckets = heapIndex.GlobalSizeBuckets;

            return BuildDomainResult(typeStats, globalBuckets);
        }

        private static MemoryDomainResult BuildDomainResult(
            Dictionary<string, CachedTypeStatistics> typeStats,
            long[]? globalSizeBuckets)
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

            static TypeSnapshot ToSnapshot(CachedTypeStatistics s)
            {
                ulong avgSize = s.Count > 0 ? s.TotalSize / (ulong)s.Count : 0;
                return new TypeSnapshot(s.TypeName, s.Count, s.TotalSize, s.LohSize,
                    AverageSize: avgSize,
                    EstimatedRetainedBytes: 0);
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
                        RangeLabel:   SizeBucketHelper.BucketLabels[i],
                        ObjectCount:  globalSizeBuckets[i],
                        TotalBytes:   bucketBytes[i]);
                }
                histogram = entries;
            }

            return new MemoryDomainResult(
                totalMemory,
                totalLohMemory,
                lohPct,
                totalObjects,
                lohObjects,
                LohThresholdBytes,
                typeStats.Count,
                bySize.Take(20).Select(ToSnapshot).ToList(),
                byCount.Take(20).Select(ToSnapshot).ToList(),
                SizeBucketHistogram: histogram);
        }
    }
}
