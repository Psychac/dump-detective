using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ArrayTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Array Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ArrayDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("array.total",       null, r.TotalArrayObjects, "objects", MetricTrendDirection.HigherIsWorse),
                new("array.total.bytes", null, r.TotalArrayBytes,   "bytes",   MetricTrendDirection.HigherIsWorse),
                new("array.loh.bytes",   null, r.LohArrayBytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
            };

            foreach (ArrayTypeProfile p in r.TopArrayTypesBySize)
            {
                metrics.Add(new("array.type.bytes", p.ElementTypeName, p.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("array.type.count", p.ElementTypeName, p.Count, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ArrayDomainResult b || current is not ArrayDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("array.total",       null, b.TotalArrayObjects, c.TotalArrayObjects, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("array.total.bytes", null, b.TotalArrayBytes,   c.TotalArrayBytes,   "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("array.loh.bytes",   null, b.LohArrayBytes,     c.LohArrayBytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


