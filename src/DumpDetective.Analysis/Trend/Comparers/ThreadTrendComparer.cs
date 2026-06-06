using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ThreadTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("thread.alive", null, r.AliveThreadCount, "threads", MetricTrendDirection.Neutral),
                new("thread.blocked", null, r.BlockedThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("thread.lock.holding", null, r.LockHoldingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("thread.exceptions", null, r.ThreadsWithActiveExceptionsCount, "threads", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in r.WaitPatternBreakdown)
                metrics.Add(new("thread.wait.category", kv.Key, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadDomainResult b || current is not ThreadDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("thread.blocked", null, b.BlockedThreadCount, c.BlockedThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("thread.lock.holding", null, b.LockHoldingThreadCount, c.LockHoldingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("thread.exceptions", null, b.ThreadsWithActiveExceptionsCount, c.ThreadsWithActiveExceptionsCount, "threads", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in c.WaitPatternBreakdown)
            {
                b.WaitPatternBreakdown.TryGetValue(kv.Key, out int bCount);
                deltas.Add(MetricDeltaHelper.Compute("thread.wait.category", kv.Key, bCount, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }
}


