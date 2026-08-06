using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCGenerationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Generation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCGenerationDomainResult r) return [];
            double gen0Pct = r.TotalObjects == 0 ? 0.0 : r.Gen0Objects * 100.0 / r.TotalObjects;
            double gen1Pct = r.TotalObjects == 0 ? 0.0 : r.Gen1Objects * 100.0 / r.TotalObjects;
            var metrics = new List<AnalyzerMetric>
            {
                new("gc.gen0.bytes", null, r.Gen0Bytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.gen0.objects", null, r.Gen0Objects, "objects", MetricTrendDirection.HigherIsWorse),
                new("gc.gen0.percent", null, gen0Pct, "%", MetricTrendDirection.HigherIsWorse),
                new("gc.gen1.bytes", null, r.Gen1Bytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.gen1.objects", null, r.Gen1Objects, "objects", MetricTrendDirection.HigherIsWorse),
                new("gc.gen1.percent", null, gen1Pct, "%", MetricTrendDirection.HigherIsWorse),
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
            double bGen0Pct = b.TotalObjects == 0 ? 0.0 : b.Gen0Objects * 100.0 / b.TotalObjects;
            double cGen0Pct = c.TotalObjects == 0 ? 0.0 : c.Gen0Objects * 100.0 / c.TotalObjects;
            double bGen1Pct = b.TotalObjects == 0 ? 0.0 : b.Gen1Objects * 100.0 / b.TotalObjects;
            double cGen1Pct = c.TotalObjects == 0 ? 0.0 : c.Gen1Objects * 100.0 / c.TotalObjects;
            return
            [
                MetricDeltaHelper.Compute("gc.gen0.bytes",      null, b.Gen0Bytes,     c.Gen0Bytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen0.objects",    null, b.Gen0Objects,   c.Gen0Objects,   "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen0.percent",    null, bGen0Pct,        cGen0Pct,        "%",       MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen1.bytes",      null, b.Gen1Bytes,     c.Gen1Bytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen1.objects",    null, b.Gen1Objects,   c.Gen1Objects,   "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen1.percent",    null, bGen1Pct,        cGen1Pct,        "%",       MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.gen2.bytes",      null, b.Gen2Bytes,     c.Gen2Bytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.bytes",       null, b.LohBytes,      c.LohBytes,      "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.percent",     null, b.LohPercent,    c.LohPercent,    "%",       MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.total.objects",   null, b.TotalObjects,  c.TotalObjects,  "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("gc.loh.objects",     null, b.LohObjects,    c.LohObjects,    "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


