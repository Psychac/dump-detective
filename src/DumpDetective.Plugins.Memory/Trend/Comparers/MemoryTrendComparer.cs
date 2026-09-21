using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class MemoryTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Memory Analysis";

        // Headline metrics (total.bytes, loh.bytes, loh.percent, unique.types) retired onto
        // ObservationTrendDiffer, backed by heap.memory-composition — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not MemoryDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();
            foreach (var t in r.TopTypes.OrderByDescending(t => t.TotalBytes).Take(10))
                metrics.Add(new("type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            foreach (var t in r.TopTypes.OrderByDescending(t => t.Count).Take(10))
                metrics.Add(new("type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
            // Histogram bucket counts — useful for spotting shifts in allocation size profile
            if (r.SizeBucketHistogram is { Count: > 0 })
            {
                for (int i = 0; i < r.SizeBucketHistogram.Count; i++)
                {
                    var b = r.SizeBucketHistogram[i];
                    metrics.Add(new($"memory.bucket.{i}.count", b.RangeLabel, b.ObjectCount, "objects", MetricTrendDirection.Neutral));
                }
            }
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not MemoryDomainResult b || current is not MemoryDomainResult c) return [];
            var deltas = new List<MetricDelta>();
            var baseTypeMap = b.TopTypes.OrderByDescending(t => t.TotalBytes).ToDictionary(t => t.TypeName, StringComparer.Ordinal);
            foreach (var t in c.TopTypes.OrderByDescending(t => t.TotalBytes))
            {
                if (baseTypeMap.TryGetValue(t.TypeName, out var bt))
                    deltas.Add(MetricDeltaHelper.Compute("type.bytes", t.TypeName, bt.TotalBytes, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }
            // Histogram bucket deltas
            if (b.SizeBucketHistogram is { Count: > 0 } && c.SizeBucketHistogram is { Count: > 0 })
            {
                int bucketCount = Math.Min(b.SizeBucketHistogram.Count, c.SizeBucketHistogram.Count);
                for (int i = 0; i < bucketCount; i++)
                {
                    deltas.Add(MetricDeltaHelper.Compute(
                        $"memory.bucket.{i}.count",
                        c.SizeBucketHistogram[i].RangeLabel,
                        b.SizeBucketHistogram[i].ObjectCount,
                        c.SizeBucketHistogram[i].ObjectCount,
                        "objects",
                        MetricTrendDirection.Neutral));
                }
            }
            return deltas;
        }
    }
}


