using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class HangTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Hang Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HangDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("hang.alive.threads", null, r.TotalAliveThreads, "threads", MetricTrendDirection.Neutral),
                new("hang.waiting.threads", null, r.WaitingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("hang.waiting.percent", null, r.WaitingPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("hang.queued.work.items", null, r.QueuedWorkItems, "items", MetricTrendDirection.HigherIsWorse),
                new("hang.pending.tasks", null, r.PendingTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                new("hang.faulted.tasks", null, r.FaultedTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                new("hang.health.score", null, r.HealthScore, "score", MetricTrendDirection.LowerIsWorse),
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
                MetricDeltaHelper.Compute("hang.waiting.percent", null, b.WaitingPercent, c.WaitingPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.waiting.threads", null, b.WaitingThreadCount, c.WaitingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.queued.work.items", null, b.QueuedWorkItems, c.QueuedWorkItems, "items", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.pending.tasks", null, b.PendingTasks, c.PendingTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.faulted.tasks", null, b.FaultedTasks, c.FaultedTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.health.score", null, b.HealthScore, c.HealthScore, "score", MetricTrendDirection.LowerIsWorse),
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


