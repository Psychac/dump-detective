using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ObjectShapeTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Object Shape Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ObjectShapeAnalyzerDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("shape.types.analyzed",    null, r.TotalTypesAnalyzed,   "types", MetricTrendDirection.Neutral),
                new("shape.avg.ref.fields",    null, r.AvgRefFieldsPerType,  "fields", MetricTrendDirection.HigherIsWorse),
                new("shape.total.gc.scan.work", null, r.TotalGcScanWork,     "pointers", MetricTrendDirection.HigherIsWorse),
                new("shape.total.gen2.gc.scan.work", null, r.TotalGen2GcScanWork, "pointers", MetricTrendDirection.HigherIsWorse),
                new("shape.ref.heavy.count",   null, r.TopReferenceHeavyTypes.Count, "types", MetricTrendDirection.HigherIsWorse),
                new("shape.balanced.count",    null, r.TopBalancedTypes.Count, "types", MetricTrendDirection.Neutral),
                new("shape.val.heavy.count",   null, r.TopValueHeavyTypes.Count,     "types", MetricTrendDirection.Neutral),
            };

            foreach (TypeShapeProfile p in r.TopReferenceHeavyTypes)
                metrics.Add(new("shape.ref.heavy.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.HigherIsWorse));
            foreach (TypeShapeProfile p in r.TopBalancedTypes)
                metrics.Add(new("shape.balanced.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.Neutral));
            foreach (TypeShapeProfile p in r.TopValueHeavyTypes)
                metrics.Add(new("shape.value.heavy.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.Neutral));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ObjectShapeAnalyzerDomainResult b || current is not ObjectShapeAnalyzerDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("shape.types.analyzed",  null, b.TotalTypesAnalyzed,            c.TotalTypesAnalyzed,            "types",  MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("shape.avg.ref.fields",  null, b.AvgRefFieldsPerType,           c.AvgRefFieldsPerType,           "fields", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.total.gc.scan.work", null, (double)b.TotalGcScanWork,     (double)c.TotalGcScanWork,     "pointers", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.total.gen2.gc.scan.work", null, (double)b.TotalGen2GcScanWork, (double)c.TotalGen2GcScanWork, "pointers", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.ref.heavy.count", null, (double)b.TopReferenceHeavyTypes.Count, (double)c.TopReferenceHeavyTypes.Count, "types", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.balanced.count", null, (double)b.TopBalancedTypes.Count,   (double)c.TopBalancedTypes.Count,   "types", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("shape.val.heavy.count", null, (double)b.TopValueHeavyTypes.Count,     (double)c.TopValueHeavyTypes.Count,     "types", MetricTrendDirection.Neutral),
            ];
        }
    }
}


