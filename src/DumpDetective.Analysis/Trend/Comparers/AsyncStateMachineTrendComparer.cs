using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AsyncStateMachineTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async State Machine Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncStateMachineDomainResult r) return [];
            return
            [
                new("statemachine.total",       null, r.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                new("statemachine.total.bytes", null, r.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AsyncStateMachineDomainResult b || current is not AsyncStateMachineDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("statemachine.total",       null, b.TotalStateMachines,     c.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.total.bytes", null, b.TotalStateMachineBytes, c.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


