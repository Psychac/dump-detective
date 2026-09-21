using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ThreadTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Analysis";

        // Headline metrics (alive/blocked/lock.holding/exceptions) retired onto ObservationTrendDiffer,
        // backed by runtime.thread-triage — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();
            foreach (var kv in r.WaitPatternBreakdown)
                metrics.Add(new("thread.wait.category", kv.Key, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadDomainResult b || current is not ThreadDomainResult c) return [];
            var deltas = new List<MetricDelta>();
            foreach (var kv in c.WaitPatternBreakdown)
            {
                b.WaitPatternBreakdown.TryGetValue(kv.Key, out int bCount);
                deltas.Add(MetricDeltaHelper.Compute("thread.wait.category", kv.Key, bCount, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }
}


