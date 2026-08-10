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
                new("dbconn.broken", null, r.BrokenConnections,"connections", MetricTrendDirection.HigherIsWorse),
                new("dbconn.unknown", null, r.UnknownStateConnections, "connections", MetricTrendDirection.HigherIsWorse),
                new("dbconn.gen2.open", null, r.Gen2OpenConnections, "connections", MetricTrendDirection.HigherIsWorse),
                new("dbconn.gen0.open", null, r.Gen0OpenConnections, "connections", MetricTrendDirection.HigherIsWorse),
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
                MetricDeltaHelper.Compute("dbconn.broken", null, b.BrokenConnections, c.BrokenConnections, "connections", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dbconn.unknown", null, b.UnknownStateConnections, c.UnknownStateConnections, "connections", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dbconn.gen2.open", null, b.Gen2OpenConnections, c.Gen2OpenConnections, "connections", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("dbconn.gen0.open", null, b.Gen0OpenConnections, c.Gen0OpenConnections, "connections", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


