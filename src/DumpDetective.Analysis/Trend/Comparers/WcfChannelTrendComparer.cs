using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class WcfChannelTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "WCF Channel Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not WcfChannelDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("wcf.total",   null, r.TotalChannels,   "channels", MetricTrendDirection.HigherIsWorse),
                new("wcf.opened",  null, r.OpenedChannels,  "channels", MetricTrendDirection.Neutral),
                new("wcf.faulted", null, r.FaultedChannels, "channels", MetricTrendDirection.HigherIsWorse),
            };

            foreach (WcfChannelTypeSummary t in r.ByType)
            {
                metrics.Add(new("wcf.type.faulted.count", t.TypeName, t.FaultedCount, "channels", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("wcf.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not WcfChannelDomainResult b || current is not WcfChannelDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("wcf.total",   null, b.TotalChannels,   c.TotalChannels,   "channels", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("wcf.opened",  null, b.OpenedChannels,  c.OpenedChannels,  "channels", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("wcf.faulted", null, b.FaultedChannels, c.FaultedChannels, "channels", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


