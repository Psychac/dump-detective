using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class DependentHandleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Dependent Handle Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DependentHandleDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("dephandle.total", null, r.DependentHandleCount, "handles", MetricTrendDirection.Neutral),
                new("dephandle.unresolved.percent", null, r.UnresolvedPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("dephandle.unresolved.count", null, r.UnresolvedTargetCount, "targets", MetricTrendDirection.HigherIsWorse)
            };

            foreach (NameCountEntry entry in r.TopSourceTypes ?? [])
                metrics.Add(new("dephandle.source.type.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopTargetTypes ?? [])
                metrics.Add(new("dephandle.target.type.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopSourceTargetEdges ?? [])
                metrics.Add(new("dephandle.edge.type.count", entry.Name, entry.Count, "edges", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not DependentHandleDomainResult b || current is not DependentHandleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("dephandle.total", null, b.DependentHandleCount, c.DependentHandleCount, "handles", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("dephandle.unresolved.percent", null, b.UnresolvedPercent, c.UnresolvedPercent, "%", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


