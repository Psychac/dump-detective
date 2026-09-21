using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SqlTransactionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "SQL Transaction Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by
        // infra.sql-transaction-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SqlTransactionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (SqlTransactionTypeSummary t in r.ByType)
                metrics.Add(new("sqltxn.type.active.count", t.TypeName, t.ActiveCount, "transactions", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}
