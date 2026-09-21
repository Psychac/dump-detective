using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class TimerLeakTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Timer Leak Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by infra.timer-population —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        // timer.logical/timer.queue were always identical (LogicalTimerCount is a legacy alias for
        // TimerQueueTimerCount — see TimerLeakSynthesisRule's own remarks), so they now both come
        // from the differ's single timer-queue.count measure, not two.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not TimerLeakDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

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

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


