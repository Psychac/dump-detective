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
            MemoryAnalysisOptions options = context.AnalysisOptions.MemoryAnalysis;
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
            byCount.Sort((a, b) =>
            {
                int byObjectCount = b.Count.CompareTo(a.Count);
                return byObjectCount != 0 ? byObjectCount : b.TotalSize.CompareTo(a.TotalSize);
            });
            var byLoh = new List<CachedTypeStatistics>(typeStats.Values);
            byLoh.Sort((a, b) =>
            {
                int byLohSize = b.LohSize.CompareTo(a.LohSize);
                return byLohSize != 0 ? byLohSize : b.TotalSize.CompareTo(a.TotalSize);
            });
            var byAverageSize = new List<CachedTypeStatistics>(typeStats.Values);
            byAverageSize.Sort((a, b) =>
            {
                ulong aAvg = a.Count > 0 ? a.TotalSize / (ulong)a.Count : 0;
                ulong bAvg = b.Count > 0 ? b.TotalSize / (ulong)b.Count : 0;
                int byAvg = bAvg.CompareTo(aAvg);
                return byAvg != 0 ? byAvg : b.TotalSize.CompareTo(a.TotalSize);
            });

            var byCompositePressure = new List<CachedTypeStatistics>(typeStats.Values);
            ulong maxTotalSize = 0;
            int maxCount = 0;
            ulong maxLohSize = 0;
            ulong maxAvgSize = 0;
            for (int i = 0; i < byCompositePressure.Count; i++)
            {
                CachedTypeStatistics stat = byCompositePressure[i];
                if (stat.TotalSize > maxTotalSize) maxTotalSize = stat.TotalSize;
                if (stat.Count > maxCount) maxCount = stat.Count;
                if (stat.LohSize > maxLohSize) maxLohSize = stat.LohSize;
                ulong avg = stat.Count > 0 ? stat.TotalSize / (ulong)stat.Count : 0;
                if (avg > maxAvgSize) maxAvgSize = avg;
            }

            int NormalizeWeight(int value) => Math.Max(0, value);
            int sizeWeight = NormalizeWeight(options.TopTypesBySizeWeight);
            int countWeight = NormalizeWeight(options.TopTypesByCountWeight);
            int lohWeight = NormalizeWeight(options.TopTypesByLohWeight);
            int avgWeight = NormalizeWeight(options.TopTypesByAverageSizeWeight);
            int compositeDenominator = sizeWeight + countWeight + lohWeight + avgWeight;
            if (compositeDenominator <= 0)
            {
                sizeWeight = 40;
                countWeight = 35;
                lohWeight = 15;
                avgWeight = 10;
                compositeDenominator = 100;
            }

            double SafeNorm(double value, double max) => max <= 0 ? 0 : value / max;
            byCompositePressure.Sort((a, b) =>
            {
                ulong aAvg = a.Count > 0 ? a.TotalSize / (ulong)a.Count : 0;
                ulong bAvg = b.Count > 0 ? b.TotalSize / (ulong)b.Count : 0;

                double aScore =
                    SafeNorm(a.TotalSize, maxTotalSize) * sizeWeight +
                    SafeNorm(a.Count, maxCount) * countWeight +
                    SafeNorm(a.LohSize, maxLohSize) * lohWeight +
                    SafeNorm(aAvg, maxAvgSize) * avgWeight;
                double bScore =
                    SafeNorm(b.TotalSize, maxTotalSize) * sizeWeight +
                    SafeNorm(b.Count, maxCount) * countWeight +
                    SafeNorm(b.LohSize, maxLohSize) * lohWeight +
                    SafeNorm(bAvg, maxAvgSize) * avgWeight;

                int byScore = bScore.CompareTo(aScore);
                if (byScore != 0)
                    return byScore;
                return b.TotalSize.CompareTo(a.TotalSize);
            });

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

            int topN = Math.Min(options.TopTypesCount, bySize.Count);
            var selectedTypes = new List<CachedTypeStatistics>(topN);
            var selectedTypeNames = new HashSet<string>(StringComparer.Ordinal);

            static int AddFromRankedList(
                List<CachedTypeStatistics> ranked,
                int take,
                HashSet<string> selectedTypeNames,
                List<CachedTypeStatistics> selectedTypes)
            {
                if (take <= 0)
                    return 0;

                int added = 0;
                for (int i = 0; i < ranked.Count && added < take; i++)
                {
                    CachedTypeStatistics stat = ranked[i];
                    if (!selectedTypeNames.Add(stat.TypeName))
                        continue;

                    selectedTypes.Add(stat);
                    added++;
                }

                return added;
            }

            int remaining = topN;

            int totalWeight = sizeWeight + countWeight + lohWeight + avgWeight;
            int ComputeQuota(int weight)
            {
                if (remaining <= 0 || weight <= 0)
                    return 0;

                int quota = (int)Math.Round((double)topN * weight / totalWeight, MidpointRounding.AwayFromZero);
                quota = Math.Max(1, quota);
                return Math.Min(remaining, quota);
            }

            int sizeQuota = ComputeQuota(sizeWeight);
            remaining -= AddFromRankedList(bySize, sizeQuota, selectedTypeNames, selectedTypes);

            int countQuota = ComputeQuota(countWeight);
            remaining -= AddFromRankedList(byCount, countQuota, selectedTypeNames, selectedTypes);

            int lohQuota = ComputeQuota(lohWeight);
            remaining -= AddFromRankedList(byLoh, lohQuota, selectedTypeNames, selectedTypes);

            int avgQuota = ComputeQuota(avgWeight);
            remaining -= AddFromRankedList(byAverageSize, avgQuota, selectedTypeNames, selectedTypes);

            if (remaining > 0)
                remaining -= AddFromRankedList(byCompositePressure, remaining, selectedTypeNames, selectedTypes);
            if (remaining > 0)
                remaining -= AddFromRankedList(bySize, remaining, selectedTypeNames, selectedTypes);
            if (remaining > 0)
                remaining -= AddFromRankedList(byCount, remaining, selectedTypeNames, selectedTypes);
            if (remaining > 0)
                remaining -= AddFromRankedList(byLoh, remaining, selectedTypeNames, selectedTypes);
            if (remaining > 0)
                AddFromRankedList(byAverageSize, remaining, selectedTypeNames, selectedTypes);

            var topTypes = new List<TypeSnapshot>(selectedTypes.Count);
            HashSet<ulong> retainedClaims = new();
            for (int i = 0; i < selectedTypes.Count; i++)
            {
                CachedTypeStatistics stat = selectedTypes[i];
                topTypes.Add(ToSnapshot(stat, EstimateRetained(heap, cache, stat.TypeName, retainedClaims), cache.GetSampleInstanceAddress(stat.TypeName) ?? 0));
            }

            return new MemoryDomainResult(
                totalMemory,
                totalLohMemory,
                lohPct,
                totalObjects,
                lohObjects,
                options.LohThresholdBytes,
                typeStats.Count,
                topTypes,
                SizeBucketHistogram: histogram);
        }

        public void Dispose() { }
    }
}
