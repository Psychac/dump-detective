using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SqlTransactionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "SQL Transaction Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SqlTransactionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("sqltxn.total",    null, r.TotalTransactions, "transactions", MetricTrendDirection.HigherIsWorse),
                new("sqltxn.active",   null, r.ActiveCount,       "transactions", MetricTrendDirection.HigherIsWorse),
                new("sqltxn.disposed", null, r.DisposedCount,     "transactions", MetricTrendDirection.Neutral),
                new("sqltxn.other",    null, r.OtherCount,        "transactions", MetricTrendDirection.HigherIsWorse),
            };

            foreach (SqlTransactionTypeSummary t in r.ByType)
                metrics.Add(new("sqltxn.type.active.count", t.TypeName, t.ActiveCount, "transactions", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not SqlTransactionDomainResult b || current is not SqlTransactionDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("sqltxn.total",    null, b.TotalTransactions, c.TotalTransactions, "transactions", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("sqltxn.active",   null, b.ActiveCount,        c.ActiveCount,       "transactions", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("sqltxn.disposed", null, b.DisposedCount,      c.DisposedCount,     "transactions", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("sqltxn.other",    null, b.OtherCount,         c.OtherCount,        "transactions", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}
