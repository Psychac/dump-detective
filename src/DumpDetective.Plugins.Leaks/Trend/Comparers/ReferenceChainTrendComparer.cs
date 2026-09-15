using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ReferenceChainTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Reference Chain Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ReferenceChainDomainResult r) return [];
            return
            [
                new("refchain.retained.percent", null, r.RetainedPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("refchain.retained.samples", null, r.RetainedSamples, "types", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ReferenceChainDomainResult b || current is not ReferenceChainDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("refchain.retained.percent", null, b.RetainedPercent, c.RetainedPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("refchain.retained.samples", null, b.RetainedSamples, c.RetainedSamples, "types", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


