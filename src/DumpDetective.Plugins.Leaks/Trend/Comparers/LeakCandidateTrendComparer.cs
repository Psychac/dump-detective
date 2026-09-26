using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class LeakCandidateTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Leak Candidate Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not LeakCandidateDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("leak.candidates.total", null, r.TotalCandidates, "candidates", MetricTrendDirection.HigherIsWorse),
                new("leak.candidates.top.score", null, TopSuspicionScore(r), "score", MetricTrendDirection.HigherIsWorse),
                new("leak.candidates.top.count", null, r.TopCandidates.Count, "candidates", MetricTrendDirection.Neutral)
            };

            foreach (LeakCandidateRecord candidate in r.TopCandidates)
            {
                metrics.Add(new("leak.candidate.type.bytes", candidate.TypeName, candidate.TotalSize, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("leak.candidate.type.score", candidate.TypeName, LeakCandidateClassifier.Assess(candidate, r.LogicalTimerCount).SuspicionScore, "score", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not LeakCandidateDomainResult b || current is not LeakCandidateDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("leak.candidates.total", null, b.TotalCandidates, c.TotalCandidates, "candidates", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.candidates.top.count", null, b.TopCandidates.Count, c.TopCandidates.Count, "candidates", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("leak.candidates.top.score", null,
                    TopSuspicionScore(b),
                    TopSuspicionScore(c),
                    "score", MetricTrendDirection.HigherIsWorse)
            ];
        }

        private static int TopSuspicionScore(LeakCandidateDomainResult result) =>
            result.TopCandidates.Count > 0 ? LeakCandidateClassifier.Assess(result.TopCandidates[0], result.LogicalTimerCount).SuspicionScore : 0;
    }
}


