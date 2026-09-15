using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Root Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCRootDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("gcroot.total.roots",           null, r.TotalRoots,                     "roots",  MetricTrendDirection.Neutral),
                new("gcroot.top.severity.score",    null, r.TopRootsBySeverity.Count > 0 ? r.TopRootsBySeverity[0].SeverityScore : 0, "score", MetricTrendDirection.HigherIsWorse),
                new("gcroot.path.capped.count",     null, r.SubgraphWalkCappedCount,           "paths",  MetricTrendDirection.Neutral),
                new("gcroot.strong.handle.count",   null, GetKindCount(r, "StrongHandle"),   "roots",  MetricTrendDirection.HigherIsWorse),
                new("gcroot.finalizer.count",       null, GetKindCount(r, "FinalizerQueue"), "roots",  MetricTrendDirection.HigherIsWorse),
            };

            foreach (RootKindSummary kind in r.ByKind)
                metrics.Add(new("gcroot.kind.count", kind.Kind, kind.Count, "roots", MetricTrendDirection.HigherIsWorse));
            foreach (RootFinding root in r.TopRootsBySeverity)
            {
                metrics.Add(new("gcroot.top.target.bytes", root.TargetTypeName, root.EstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("gcroot.top.target.severity", root.TargetTypeName, root.SeverityScore, "score", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCRootDomainResult b || current is not GCRootDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gcroot.total.roots",         null, b.TotalRoots,                                                                   c.TotalRoots,                                                                   "roots",  MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("gcroot.strong.handle.count", null, (double)GetKindCount(b, "StrongHandle"),   (double)GetKindCount(c, "StrongHandle"),   "roots",  MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gcroot.finalizer.count",     null, (double)GetKindCount(b, "FinalizerQueue"), (double)GetKindCount(c, "FinalizerQueue"), "roots",  MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gcroot.path.capped.count",   null, (double)b.SubgraphWalkCappedCount,         (double)c.SubgraphWalkCappedCount,           "paths",  MetricTrendDirection.Neutral),
            ];
        }

        private static int GetKindCount(GCRootDomainResult r, string kind)
        {
            foreach (RootKindSummary ks in r.ByKind)
                if (ks.Kind == kind) return ks.Count;
            return 0;
        }
    }
}


