using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AllocationPatternTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Allocation Pattern Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AllocationPatternDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("alloc.gen0.count.pct",         null, r.Gen0CountPct,          "%",     MetricTrendDirection.Neutral),
                new("alloc.gen1.count.pct",         null, r.Gen1CountPct,          "%",     MetricTrendDirection.Neutral),
                new("alloc.gen2.count.pct",         null, r.Gen2CountPct,          "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.loh.count.pct",          null, r.LohCountPct,           "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.gen0.size.pct",          null, r.Gen0SizePct,           "%",     MetricTrendDirection.Neutral),
                new("alloc.gen1.size.pct",          null, r.Gen1SizePct,           "%",     MetricTrendDirection.Neutral),
                new("alloc.gen2.size.pct",          null, r.Gen2SizePct,           "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.loh.size.pct",           null, r.LohSizePct,            "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.gc.pressure",            null, (double)r.GCPressure,    "level", MetricTrendDirection.HigherIsWorse),
                new("alloc.promotion.pressure",     null, r.PromotionPressureScore,"score", MetricTrendDirection.HigherIsWorse),
                new("alloc.finalizable.type.count", null, r.FinalizableTypeCount,  "types", MetricTrendDirection.HigherIsWorse),
                new("alloc.finalizable.bytes",      null, r.FinalizableBytes,      "bytes", MetricTrendDirection.HigherIsWorse),
            };

            if (r.LohSizeBands is not null)
            {
                foreach (var band in r.LohSizeBands)
                    metrics.Add(new("alloc.loh.band.bytes", band.RangeLabel, band.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            foreach (TypeAllocationProfile p in r.TopTransientTypes)
                metrics.Add(new("alloc.transient.type.gen0.count", p.TypeName, p.Gen0Count, "objects", MetricTrendDirection.Neutral));
            foreach (TypeAllocationProfile p in r.TopShortishTypes)
                metrics.Add(new("alloc.shortish.type.gen1.count", p.TypeName, p.Gen1Count, "objects", MetricTrendDirection.Neutral));
            foreach (TypeAllocationProfile p in r.TopLongLivedTypes)
            {
                metrics.Add(new("alloc.longlived.type.gen2.count", p.TypeName, p.Gen2Count, "objects", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("alloc.longlived.type.ratio", p.TypeName, p.LongLivedRatio, "ratio", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AllocationPatternDomainResult b || current is not AllocationPatternDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("alloc.gen0.count.pct",     null, b.Gen0CountPct,          c.Gen0CountPct,          "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen1.count.pct",     null, b.Gen1CountPct,          c.Gen1CountPct,          "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen2.count.pct",     null, b.Gen2CountPct,          c.Gen2CountPct,          "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.loh.count.pct",      null, b.LohCountPct,           c.LohCountPct,           "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.gen0.size.pct",      null, b.Gen0SizePct,           c.Gen0SizePct,           "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen1.size.pct",      null, b.Gen1SizePct,           c.Gen1SizePct,           "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen2.size.pct",      null, b.Gen2SizePct,           c.Gen2SizePct,           "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.loh.size.pct",       null, b.LohSizePct,            c.LohSizePct,            "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.gc.pressure",        null, (double)b.GCPressure,    (double)c.GCPressure,    "level", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.promotion.pressure", null, b.PromotionPressureScore,c.PromotionPressureScore,"score", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.finalizable.type.count", null, b.FinalizableTypeCount, c.FinalizableTypeCount, "types", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.finalizable.bytes",      null, b.FinalizableBytes,     c.FinalizableBytes,     "bytes", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


