using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class DominatorTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Dominator Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DominatorDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("dominator.candidates", null, r.CandidateCount, "candidates", MetricTrendDirection.HigherIsWorse),
                new("dominator.analyzed", null, r.AnalyzedCount, "types", MetricTrendDirection.Neutral),
                new("dominator.retained.bytes", null, r.TotalEstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("dominator.top.count", null, r.TopDominatorTypes.Count, "types", MetricTrendDirection.Neutral),
                new("leak.highly.referenced", null, r.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse),
                new("leak.highly.referenced.bytes", null, r.TopHighlyReferencedTotalBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            };

            foreach (TypeSnapshot t in r.TopDominatorTypes)
                metrics.Add(new("dominator.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));

            foreach (RetentionTypeSnapshot t in r.TopRetentionTypes ?? [])
            {
                metrics.Add(new("leak.retention.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("leak.retention.type.count", t.TypeName, t.ObjectCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not DominatorDomainResult b || current is not DominatorDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("dominator.candidates", null, b.CandidateCount, c.CandidateCount, "candidates", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dominator.analyzed", null, b.AnalyzedCount, c.AnalyzedCount, "types", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("dominator.retained.bytes", null, b.TotalEstimatedRetainedBytes, c.TotalEstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dominator.top.count", null, b.TopDominatorTypes.Count, c.TopDominatorTypes.Count, "types", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("leak.highly.referenced", null, b.HighlyReferencedObjectCount, c.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.highly.referenced.bytes", null, b.TopHighlyReferencedTotalBytes, c.TopHighlyReferencedTotalBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}
