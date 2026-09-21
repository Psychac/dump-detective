using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class HangTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Hang Analysis";

        // hang.alive.threads/.waiting.threads/.waiting.percent/.queued.work.items/.pending.tasks/
        // .faulted.tasks retired onto ObservationTrendDiffer, backed by runtime.hang-pressure — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        // hang.health.score stays hand-written permanently — it's a HangHealthClassifier composite
        // score, banned from raw Observations by the purity rule (Bucket C genuine exception).
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HangDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("hang.health.score", null, HangHealthClassifier.ComputeHealthScore(r), "score", MetricTrendDirection.LowerIsWorse),
            };
            foreach (var kv in r.WaitCategoryBreakdown)
                metrics.Add(new("hang.wait.category", kv.Key, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not HangDomainResult b || current is not HangDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("hang.health.score", null, HangHealthClassifier.ComputeHealthScore(b), HangHealthClassifier.ComputeHealthScore(c), "score", MetricTrendDirection.LowerIsWorse),
            };
            foreach (var kv in c.WaitCategoryBreakdown)
            {
                b.WaitCategoryBreakdown.TryGetValue(kv.Key, out int baseCount);
                deltas.Add(MetricDeltaHelper.Compute("hang.wait.category", kv.Key, baseCount, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }
}


