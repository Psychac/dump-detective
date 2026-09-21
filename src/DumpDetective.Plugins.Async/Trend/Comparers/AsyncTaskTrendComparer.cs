using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AsyncTaskTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async Task Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by async.task-population —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncTaskDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (NameCountEntry entry in r.TopPendingTaskTypes)
                metrics.Add(new("task.pending.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopFaultedTaskTypes)
                metrics.Add(new("task.faulted.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopContinuationTypes)
                metrics.Add(new("task.continuation.type.count", entry.Name, entry.Count, "tasks", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


