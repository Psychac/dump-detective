using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Root Analysis";

        // total.roots/strong.handle.count/finalizer.count/path.capped.count retired onto
        // ObservationTrendDiffer, backed by root.population — see ObservationDifferAnalyzerOwnership
        // and docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        // gcroot.top.severity.score stays hand-written permanently — it's a
        // GCRootSeverityClassifier composite score, banned from raw Observations by the purity rule
        // (Bucket C genuine exception).
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCRootDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("gcroot.top.severity.score", null, r.TopRootsBySeverity.Count > 0 ? GCRootSeverityClassifier.ComputeSeverity(r.TopRootsBySeverity[0].EstimatedRetainedBytes, r.TopRootsBySeverity[0].RootKind) : 0, "score", MetricTrendDirection.HigherIsWorse),
            };

            foreach (RootKindSummary kind in r.ByKind)
                metrics.Add(new("gcroot.kind.count", kind.Kind, kind.Count, "roots", MetricTrendDirection.HigherIsWorse));
            foreach (RootFinding root in r.TopRootsBySeverity)
            {
                metrics.Add(new("gcroot.top.target.bytes", root.TargetTypeName, root.EstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("gcroot.top.target.severity", root.TargetTypeName, GCRootSeverityClassifier.ComputeSeverity(root.EstimatedRetainedBytes, root.RootKind), "score", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


