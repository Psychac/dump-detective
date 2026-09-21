using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class WcfChannelTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "WCF Channel Analysis";

        // Headline metrics (total/opening/opened/faulted/closing) retired onto ObservationTrendDiffer,
        // backed by infra.wcf-channel-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not WcfChannelDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (WcfChannelTypeSummary t in r.ByType)
            {
                metrics.Add(new("wcf.type.faulted.count", t.TypeName, t.FaultedCount, "channels", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("wcf.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


