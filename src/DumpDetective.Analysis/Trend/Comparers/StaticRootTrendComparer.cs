using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class StaticRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Static Root Leak Detection";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StaticRootDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("static.root.count", null, r.RootCount, "roots", MetricTrendDirection.HigherIsWorse),
                new("static.root.retained.bytes", null, r.TotalRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            };

            foreach (StaticRootSnapshot root in r.TopRootsByRetainedBytes ?? [])
                metrics.Add(new("static.root.byname.bytes", root.RootDescription, root.TotalMemoryImpact, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not StaticRootDomainResult b || current is not StaticRootDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("static.root.count", null, b.RootCount, c.RootCount, "roots", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("static.root.retained.bytes", null, b.TotalRetainedBytes, c.TotalRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


