using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class RetentionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Retention Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not RetentionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("leak.highly.referenced", null, r.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse),
                new("leak.highly.referenced.bytes", null, r.TopHighlyReferencedTotalBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            };

            foreach (RetentionTypeSnapshot t in r.TopRetentionTypes ?? [])
            {
                metrics.Add(new("leak.retention.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("leak.retention.type.count", t.TypeName, t.ObjectCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not RetentionDomainResult b || current is not RetentionDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("leak.highly.referenced", null, b.HighlyReferencedObjectCount, c.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.highly.referenced.bytes", null, b.TopHighlyReferencedTotalBytes, c.TopHighlyReferencedTotalBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


