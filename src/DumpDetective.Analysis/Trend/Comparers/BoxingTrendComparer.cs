using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class BoxingTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Boxing Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not BoxingDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("boxing.total.objects",     null, r.TotalBoxedObjects,       "objects", MetricTrendDirection.HigherIsWorse),
                new("boxing.total.bytes",       null, r.TotalBoxedBytes,         "bytes",   MetricTrendDirection.HigherIsWorse),
                new("boxing.enum.count",        null, r.BoxedEnumCount,          "objects", MetricTrendDirection.HigherIsWorse),
                new("boxing.enum.bytes",        null, r.BoxedEnumBytes,          "bytes",   MetricTrendDirection.HigherIsWorse),
                new("boxing.oversized.count",   null, r.OversizedValueTypeCount, "objects", MetricTrendDirection.HigherIsWorse),
            };

            foreach (BoxedTypeEntry entry in r.TopBoxedTypes)
            {
                metrics.Add(new("boxing.type.bytes", entry.ValueTypeName, entry.TotalBoxBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("boxing.type.count", entry.ValueTypeName, entry.BoxCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not BoxingDomainResult b || current is not BoxingDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("boxing.total.objects",   null, b.TotalBoxedObjects,       c.TotalBoxedObjects,       "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("boxing.total.bytes",     null, b.TotalBoxedBytes,         c.TotalBoxedBytes,         "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("boxing.enum.count",      null, b.BoxedEnumCount,          c.BoxedEnumCount,          "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("boxing.enum.bytes",      null, b.BoxedEnumBytes,          c.BoxedEnumBytes,          "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("boxing.oversized.count", null, b.OversizedValueTypeCount, c.OversizedValueTypeCount, "objects", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


