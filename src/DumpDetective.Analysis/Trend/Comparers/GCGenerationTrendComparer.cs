using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCGenerationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Generation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCGenerationDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("gc.gen2.bytes", null, r.Gen2Bytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.loh.bytes", null, r.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.loh.percent", null, r.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("gc.total.objects", null, r.TotalObjects, "objects", MetricTrendDirection.Neutral),
                new("gc.loh.objects", null, r.LohObjects, "objects", MetricTrendDirection.HigherIsWorse)
            };

            foreach (TypeSnapshot t in r.TopLohTypes)
            {
                metrics.Add(new("gc.loh.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("gc.loh.type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCGenerationDomainResult b || current is not GCGenerationDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gc.gen2.bytes",     null, b.Gen2Bytes,     c.Gen2Bytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.bytes",      null, b.LohBytes,      c.LohBytes,      "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.percent",    null, b.LohPercent,    c.LohPercent,    "%",       MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.total.objects",  null, b.TotalObjects,  c.TotalObjects,  "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("gc.loh.objects",    null, b.LohObjects,    c.LohObjects,    "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


