using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class DbConnectionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "DB Connection Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by
        // infra.db-connection-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DbConnectionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (DbConnectionTypeSummary t in r.ByType)
            {
                metrics.Add(new("dbconn.type.open.count", t.TypeName, t.OpenCount, "connections", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("dbconn.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


