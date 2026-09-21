using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class BoxingTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Boxing Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by type.boxing-pressure —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not BoxingDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (BoxedTypeEntry entry in r.TopBoxedTypes)
            {
                metrics.Add(new("boxing.type.bytes", entry.ValueTypeName, entry.TotalBoxBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("boxing.type.count", entry.ValueTypeName, entry.BoxCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


