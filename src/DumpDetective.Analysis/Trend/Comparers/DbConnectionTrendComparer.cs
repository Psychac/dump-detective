using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class DbConnectionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "DB Connection Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DbConnectionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("dbconn.total",  null, r.TotalConnections, "connections", MetricTrendDirection.HigherIsWorse),
                new("dbconn.open",   null, r.OpenConnections,  "connections", MetricTrendDirection.HigherIsWorse),
                new("dbconn.closed", null, r.ClosedConnections,"connections", MetricTrendDirection.Neutral),
            };

            foreach (DbConnectionTypeSummary t in r.ByType)
            {
                metrics.Add(new("dbconn.type.open.count", t.TypeName, t.OpenCount, "connections", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("dbconn.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not DbConnectionDomainResult b || current is not DbConnectionDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("dbconn.total",  null, b.TotalConnections,  c.TotalConnections,  "connections", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dbconn.open",   null, b.OpenConnections,   c.OpenConnections,   "connections", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dbconn.closed", null, b.ClosedConnections, c.ClosedConnections, "connections", MetricTrendDirection.Neutral),
            ];
        }
    }
}


