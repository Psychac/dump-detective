using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class TimerLeakTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Timer Leak Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not TimerLeakDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("timer.logical",    null, r.LogicalTimerCount,     "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.total",      null, r.TotalTimers,          "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.threading",  null, r.ThreadingTimerCount,  "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.queue",      null, r.TimerQueueTimerCount, "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.holder",     null, r.TimerHolderCount,     "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.periodic",   null, r.PeriodicTimerCount,   "objects", MetricTrendDirection.HigherIsWorse),
                new("timer.bytes",      null, r.TotalBytes,           "bytes",   MetricTrendDirection.HigherIsWorse),
            };

            foreach (TimerObjectTypeSummary t in r.ByType)
            {
                metrics.Add(new("timer.type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("timer.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            foreach ((string bucket, int count) in r.IntervalHistogram)
            {
                metrics.Add(new("timer.interval.bucket", bucket, count, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not TimerLeakDomainResult b || current is not TimerLeakDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("timer.logical",   null, b.LogicalTimerCount,     c.LogicalTimerCount,     "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.total",     null, b.TotalTimers,          c.TotalTimers,          "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.threading", null, b.ThreadingTimerCount,  c.ThreadingTimerCount,  "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.queue",     null, b.TimerQueueTimerCount, c.TimerQueueTimerCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.holder",    null, b.TimerHolderCount,     c.TimerHolderCount,     "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.periodic",  null, b.PeriodicTimerCount,   c.PeriodicTimerCount,   "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("timer.bytes",     null, b.TotalBytes,           c.TotalBytes,           "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


