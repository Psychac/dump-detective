using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class StaticRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Static Root Leak Detection";

        // Headline metrics (root.count, retained.bytes) retired onto ObservationTrendDiffer, backed
        // by static-root.population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StaticRootDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (StaticRootSnapshot root in r.TopRootsByRetainedBytes ?? [])
                metrics.Add(new("static.root.byname.bytes", root.RootDescription, root.TotalMemoryImpact, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


