using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SqlConnectionPoolTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "SQL Connection Pool Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SqlConnectionPoolDomainResult r) return [];
            return
            [
                new("sqlpool.total",             null, r.TotalPools,        "pools", MetricTrendDirection.Neutral),
                new("sqlpool.near_capacity",      null, r.PoolsNearCapacity, "pools", MetricTrendDirection.HigherIsWorse),
                new("sqlpool.max_utilization_pct", null, MaxUtilizationPct(r), "%",   MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not SqlConnectionPoolDomainResult b || current is not SqlConnectionPoolDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("sqlpool.total",             null, b.TotalPools,          c.TotalPools,          "pools", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("sqlpool.near_capacity",      null, b.PoolsNearCapacity,   c.PoolsNearCapacity,   "pools", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("sqlpool.max_utilization_pct", null, MaxUtilizationPct(b), MaxUtilizationPct(c), "%",     MetricTrendDirection.HigherIsWorse),
            ];
        }

        private static double MaxUtilizationPct(SqlConnectionPoolDomainResult r)
        {
            double max = 0;
            foreach (var pool in r.Pools)
            {
                double pct = SqlConnectionPoolAnalyzer.UtilizationPercent(pool);
                if (pct > max) max = pct;
            }
            return max;
        }
    }
}
