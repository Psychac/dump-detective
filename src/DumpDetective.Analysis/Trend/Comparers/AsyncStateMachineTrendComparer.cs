using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AsyncStateMachineTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async State Machine Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncStateMachineDomainResult r) return [];
            // TotalGen2Count is summed over all candidate types (TypeCandidateLimit), matching the
            // scope of TotalStateMachines — using TopStateMachineTypes (TopTypeLimit) here would
            // understate the fraction whenever candidate types exceed TopTypeLimit.
            double gen2Fraction = r.TotalStateMachines == 0 ? 0.0 : r.TotalGen2Count * 100.0 / r.TotalStateMachines;
            return
            [
                new("statemachine.total",       null, r.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                new("statemachine.total.bytes", null, r.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
                new("statemachine.gen2.count", null, r.TotalGen2Count, "objects", MetricTrendDirection.HigherIsWorse),
                new("statemachine.gen2.fraction", null, gen2Fraction, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AsyncStateMachineDomainResult b || current is not AsyncStateMachineDomainResult c) return [];

            double bGen2Fraction = b.TotalStateMachines == 0 ? 0.0 : b.TotalGen2Count * 100.0 / b.TotalStateMachines;
            double cGen2Fraction = c.TotalStateMachines == 0 ? 0.0 : c.TotalGen2Count * 100.0 / c.TotalStateMachines;

            return
            [
                MetricDeltaHelper.Compute("statemachine.total",       null, b.TotalStateMachines,     c.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.total.bytes", null, b.TotalStateMachineBytes, c.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.gen2.count", null, b.TotalGen2Count, c.TotalGen2Count, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.gen2.fraction", null, bGen2Fraction, cGen2Fraction, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


