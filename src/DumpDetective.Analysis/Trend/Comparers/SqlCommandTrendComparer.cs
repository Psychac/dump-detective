using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SqlCommandTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "SQL Command Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SqlCommandDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("sqlcmd.total",    null, r.TotalCommands, "commands", MetricTrendDirection.HigherIsWorse),
                new("sqlcmd.active",   null, r.ActiveCount,   "commands", MetricTrendDirection.HigherIsWorse),
                new("sqlcmd.disposed", null, r.DisposedCount, "commands", MetricTrendDirection.Neutral),
            };

            foreach (SqlCommandTypeSummary t in r.ByType)
                metrics.Add(new("sqlcmd.type.active.count", t.TypeName, t.ActiveCount, "commands", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not SqlCommandDomainResult b || current is not SqlCommandDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("sqlcmd.total",    null, b.TotalCommands, c.TotalCommands, "commands", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("sqlcmd.active",   null, b.ActiveCount,   c.ActiveCount,   "commands", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("sqlcmd.disposed", null, b.DisposedCount, c.DisposedCount, "commands", MetricTrendDirection.Neutral),
            ];
        }
    }
}
