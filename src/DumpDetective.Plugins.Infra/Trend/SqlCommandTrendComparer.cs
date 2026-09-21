using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SqlCommandTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "SQL Command Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by
        // infra.sql-command-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SqlCommandDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (SqlCommandTypeSummary t in r.ByType)
                metrics.Add(new("sqlcmd.type.active.count", t.TypeName, t.ActiveCount, "commands", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}
