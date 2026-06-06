using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ThreadStackClusterTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Stack Signature Clustering";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadStackClusterDomainResult r) return [];
            return
            [
                new("cluster.alive.threads", null, r.AliveThreadCount, "threads", MetricTrendDirection.Neutral),
                new("cluster.unique", null, r.UniqueClusters, "clusters", MetricTrendDirection.Neutral),
                new("cluster.diversity.percent", null, r.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadStackClusterDomainResult b || current is not ThreadStackClusterDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("cluster.diversity.percent", null, b.DiversityPercent, c.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse),
                MetricDeltaHelper.Compute("cluster.unique", null, b.UniqueClusters, c.UniqueClusters, "clusters", MetricTrendDirection.Neutral)
            ];
        }
    }
}


