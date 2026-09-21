using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class DominatorTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Dominator Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by type.retention-population
        // — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        // dominator.analyzed/dominator.top.count were always identical (AnalyzedCount and
        // TopDominatorTypes.Count are both assigned from the same topTypes.Count), so they now
        // both come from the differ's single analyzed.count measure, not two.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DominatorDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (TypeSnapshot t in r.TopDominatorTypes)
            {
                metrics.Add(new("dominator.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                if (t.EstimatedRetainedBytes > 0)
                    metrics.Add(new("dominator.type.retained.bytes", t.TypeName, t.EstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            foreach (RetentionTypeSnapshot t in r.TopRetentionTypes ?? [])
            {
                metrics.Add(new("leak.retention.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("leak.retention.type.count", t.TypeName, t.ObjectCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}
