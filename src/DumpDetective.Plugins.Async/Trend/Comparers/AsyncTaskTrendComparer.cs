using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AsyncTaskTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async Task Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncTaskDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("task.total",             null, r.TotalTasks,            "tasks",   MetricTrendDirection.Neutral),
                new("task.pending",           null, r.PendingTasks,          "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.faulted",           null, r.FaultedTasks,          "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.canceled",          null, r.CanceledTasks,         "tasks",   MetricTrendDirection.Neutral),
                new("task.orphaned",          null, r.OrphanedTasks,         "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.chain.depth.max",   null, r.MaxContinuationDepth,  "depth",   MetricTrendDirection.HigherIsWorse),
                new("task.chain.depth.avg",   null, r.AvgContinuationDepth,  "depth",   MetricTrendDirection.HigherIsWorse),
            };

            foreach (NameCountEntry entry in r.TopPendingTaskTypes)
                metrics.Add(new("task.pending.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopFaultedTaskTypes)
                metrics.Add(new("task.faulted.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopContinuationTypes)
                metrics.Add(new("task.continuation.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AsyncTaskDomainResult b || current is not AsyncTaskDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("task.total",            null, b.TotalTasks,           c.TotalTasks,           "tasks", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("task.pending",          null, b.PendingTasks,          c.PendingTasks,         "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.faulted",          null, b.FaultedTasks,          c.FaultedTasks,         "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.canceled",         null, b.CanceledTasks,         c.CanceledTasks,        "tasks", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("task.orphaned",         null, b.OrphanedTasks,         c.OrphanedTasks,        "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.chain.depth.max",  null, b.MaxContinuationDepth,  c.MaxContinuationDepth, "depth", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.chain.depth.avg",  null, b.AvgContinuationDepth,  c.AvgContinuationDepth, "depth", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


