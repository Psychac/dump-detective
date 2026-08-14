using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AsyncStateMachineTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async State Machine Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncStateMachineDomainResult r) return [];
            long totalGen2Count = 0;
            foreach (var profile in r.TopStateMachineTypes)
            {
                totalGen2Count += profile.Gen2Count;
            }
            double gen2Fraction = r.TotalStateMachines == 0 ? 0.0 : totalGen2Count * 100.0 / r.TotalStateMachines;
            return
            [
                new("statemachine.total",       null, r.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                new("statemachine.total.bytes", null, r.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
                new("statemachine.gen2.count", null, totalGen2Count, "objects", MetricTrendDirection.HigherIsWorse),
                new("statemachine.gen2.fraction", null, gen2Fraction, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AsyncStateMachineDomainResult b || current is not AsyncStateMachineDomainResult c) return [];
            long bTotalGen2Count = 0;
            foreach (var profile in b.TopStateMachineTypes)
            {
                bTotalGen2Count += profile.Gen2Count;
            }
            double bGen2Fraction = b.TotalStateMachines == 0 ? 0.0 : bTotalGen2Count * 100.0 / b.TotalStateMachines;

            long cTotalGen2Count = 0;
            foreach (var profile in c.TopStateMachineTypes)
            {
                cTotalGen2Count += profile.Gen2Count;
            }
            double cGen2Fraction = c.TotalStateMachines == 0 ? 0.0 : cTotalGen2Count * 100.0 / c.TotalStateMachines;

            return
            [
                MetricDeltaHelper.Compute("statemachine.total",       null, b.TotalStateMachines,     c.TotalStateMachines,     "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.total.bytes", null, b.TotalStateMachineBytes, c.TotalStateMachineBytes, "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.gen2.count", null, bTotalGen2Count, cTotalGen2Count, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("statemachine.gen2.fraction", null, bGen2Fraction, cGen2Fraction, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


