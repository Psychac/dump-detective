using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class LockGraphTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Lock Graph Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not LockGraphDomainResult r) return [];
            return
            [
                new("lock.held", null, r.TotalHeldLocks, "locks", MetricTrendDirection.Neutral),
                new("lock.contested", null, r.ContestedLockCount, "locks", MetricTrendDirection.HigherIsWorse),
                new("lock.max.waiters", null, r.MaxWaitersOnSingleLock, "threads", MetricTrendDirection.HigherIsWorse),
                new("lock.deadlock.candidates", null, r.DeadlockCandidateCount, "threads", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not LockGraphDomainResult b || current is not LockGraphDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("lock.contested", null, b.ContestedLockCount, c.ContestedLockCount, "locks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("lock.max.waiters", null, b.MaxWaitersOnSingleLock, c.MaxWaitersOnSingleLock, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("lock.deadlock.candidates", null, b.DeadlockCandidateCount, c.DeadlockCandidateCount, "threads", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


