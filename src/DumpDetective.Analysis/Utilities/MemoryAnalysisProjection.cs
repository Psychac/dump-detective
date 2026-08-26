using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Utilities;

internal sealed record MemoryAnalysisProjectionResult(
    ulong TotalMemory,
    ulong TotalLohMemory,
    long TotalObjects,
    long LohObjects,
    double LohPercent,
    // Every distinct type, sorted by total bytes descending (§9.27) — no arbitrary Top-N
    // selection or weighted-composite quota merge. Report-width limiting, if any, is a
    // render-layer concern.
    IReadOnlyList<CachedTypeStatistics> AllTypesBySize,
    IReadOnlyList<SizeBucketEntry>? Histogram,
    ulong Top1Bytes,
    ulong Top5Bytes,
    ulong Top10Bytes,
    long SmallObjectCount,
    ulong SmallObjectBytes,
    double ObjectsPerMb,
    double MemoryPressureScore,
    double LohFragmentationRatio,
    double LohPressureScore,
    double ConcentrationPressureScore,
    double SmallObjectPressureScore,
    double DensityPressureScore);

internal static class MemoryAnalysisProjection
{
    public static MemoryAnalysisProjectionResult Build(
        Dictionary<string, CachedTypeStatistics> typeStats,
        long[]? globalSizeBuckets)
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

        return new MemoryAnalysisProjectionResult(
            totalMemory,
            totalLohMemory,
            totalObjects,
            lohObjects,
            lohPct,
            bySize,
            histogram,
            top1Bytes,
            top5Bytes,
            top10Bytes,
            smallObjectCount,
            smallObjectBytes,
            objectsPerMb,
            memoryPressureScore,
            0,
            Math.Round(lohPressure * 100.0, 1),
            Math.Round(concentrationPressure * 100.0, 1),
            Math.Round(smallObjectPressure * 100.0, 1),
            Math.Round(densityPressure * 100.0, 1));
    }
}
