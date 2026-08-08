using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Utilities;

internal sealed record MemoryAnalysisProjectionResult(
    ulong TotalMemory,
    ulong TotalLohMemory,
    long TotalObjects,
    long LohObjects,
    double LohPercent,
    IReadOnlyList<CachedTypeStatistics> SelectedTypes,
    IReadOnlyList<SizeBucketEntry>? Histogram,
    ulong Top1Bytes,
    ulong Top5Bytes,
    ulong Top10Bytes,
    long SmallObjectCount,
    ulong SmallObjectBytes,
    double ObjectsPerMb,
    double MemoryPressureScore,
    double LohFragmentationRatio);

internal static class MemoryAnalysisProjection
{
    public static MemoryAnalysisProjectionResult Build(
        Dictionary<string, CachedTypeStatistics> typeStats,
        long[]? globalSizeBuckets,
        MemoryAnalysisOptions options)
    {
        ulong totalMemory = 0;
        ulong totalLohMemory = 0;
        long totalObjects = 0;
        long lohObjects = 0;
        foreach (CachedTypeStatistics stat in typeStats.Values)
        {
            totalMemory += stat.TotalSize;
            totalLohMemory += stat.LohSize;
            totalObjects += stat.Count;
            lohObjects += stat.LohCount;
        }

        double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;

        var bySize = new List<CachedTypeStatistics>(typeStats.Values);
        bySize.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));

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

        int sizeWeight = Math.Max(0, options.TopTypesBySizeWeight);
        int countWeight = Math.Max(0, options.TopTypesByCountWeight);
        int lohWeight = Math.Max(0, options.TopTypesByLohWeight);
        int avgWeight = Math.Max(0, options.TopTypesByAverageSizeWeight);
        int compositeDenominator = sizeWeight + countWeight + lohWeight + avgWeight;
        if (compositeDenominator <= 0)
        {
            sizeWeight = 40;
            countWeight = 35;
            lohWeight = 15;
            avgWeight = 10;
            compositeDenominator = 100;
        }

        static double SafeNorm(double value, double max) => max <= 0 ? 0 : value / max;
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

        IReadOnlyList<SizeBucketEntry>? histogram = null;
        if (globalSizeBuckets is { Length: >= SizeBucketHelper.BucketCount })
        {
            var entries = new SizeBucketEntry[SizeBucketHelper.BucketCount];
            var bucketBytes = new ulong[SizeBucketHelper.BucketCount];
            foreach (CachedTypeStatistics stat in typeStats.Values)
            {
                if (stat.Count <= 0)
                    continue;

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

        static double PercentOf(ulong part, ulong total) => total == 0 ? 0 : part * 100.0 / total;
        static double PercentOfCount(long part, long total) => total == 0 ? 0 : part * 100.0 / total;

        ulong top1Bytes = bySize.Count > 0 ? bySize[0].TotalSize : 0;
        ulong top5Bytes = 0;
        for (int i = 0; i < Math.Min(5, bySize.Count); i++)
            top5Bytes += bySize[i].TotalSize;
        ulong top10Bytes = 0;
        for (int i = 0; i < Math.Min(10, bySize.Count); i++)
            top10Bytes += bySize[i].TotalSize;

        long smallObjectCount = 0;
        ulong smallObjectBytes = 0;
        if (histogram is not null)
        {
            int smallBucketCount = Math.Min(3, histogram.Count);
            for (int i = 0; i < smallBucketCount; i++)
            {
                smallObjectCount += histogram[i].ObjectCount;
                smallObjectBytes += histogram[i].TotalBytes;
            }
        }
        else
        {
            foreach (CachedTypeStatistics stat in typeStats.Values)
            {
                if (stat.Count <= 0)
                    continue;

                ulong avgSize = stat.TotalSize / (ulong)stat.Count;
                if (SizeBucketHelper.GetBucketIndex(avgSize) <= 2)
                {
                    smallObjectCount += stat.Count;
                    smallObjectBytes += stat.TotalSize;
                }
            }
        }

        double objectsPerMb = totalMemory == 0 ? 0 : totalObjects / (totalMemory / (1024.0 * 1024.0));

        static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        double lohPressure = Clamp01(lohPct / 35.0);
        double concentrationPressure = Clamp01(PercentOf(top5Bytes, totalMemory) / 70.0);
        double smallObjectPressure = Clamp01((0.7 * (PercentOfCount(smallObjectCount, totalObjects) / 85.0))
                                           + (0.3 * (PercentOf(smallObjectBytes, totalMemory) / 45.0)));
        double densityPressure = Clamp01(objectsPerMb / 12_000.0);

        double memoryPressureScore = Math.Round(
            (lohPressure * 0.35
           + concentrationPressure * 0.30
           + smallObjectPressure * 0.20
           + densityPressure * 0.15) * 100.0,
            1);

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

        if (remaining > 0)
            remaining -= AddFromRankedList(byCompositePressure, remaining, selectedTypeNames, selectedTypes);
        if (remaining > 0)
            remaining -= AddFromRankedList(bySize, remaining, selectedTypeNames, selectedTypes);

        return new MemoryAnalysisProjectionResult(
            totalMemory,
            totalLohMemory,
            totalObjects,
            lohObjects,
            lohPct,
            selectedTypes,
            histogram,
            top1Bytes,
            top5Bytes,
            top10Bytes,
            smallObjectCount,
            smallObjectBytes,
            objectsPerMb,
            memoryPressureScore,
            0);
    }
}
