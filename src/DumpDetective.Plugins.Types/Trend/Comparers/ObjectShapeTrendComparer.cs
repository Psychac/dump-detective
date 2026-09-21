using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ObjectShapeTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Object Shape Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by type.shape-composition —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ObjectShapeAnalyzerDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (TypeShapeProfile p in r.TopReferenceHeavyTypes)
                metrics.Add(new("shape.ref.heavy.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.HigherIsWorse));
            foreach (TypeShapeProfile p in r.TopBalancedTypes)
                metrics.Add(new("shape.balanced.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.Neutral));
            foreach (TypeShapeProfile p in r.TopValueHeavyTypes)
                metrics.Add(new("shape.value.heavy.type.ratio", p.TypeName, p.ReferenceFieldRatio, "ratio", MetricTrendDirection.Neutral));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


