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
                new("leak.candidates.top.score", null, r.TopCandidates.Count > 0 ? r.TopCandidates[0].SuspicionScore : 0, "score", MetricTrendDirection.HigherIsWorse),
                new("leak.candidates.top.count", null, r.TopCandidates.Count, "candidates", MetricTrendDirection.Neutral)
            };

            foreach (LeakCandidateRecord candidate in r.TopCandidates)
            {
                metrics.Add(new("leak.candidate.type.bytes", candidate.TypeName, candidate.TotalSize, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("leak.candidate.type.score", candidate.TypeName, candidate.SuspicionScore, "score", MetricTrendDirection.HigherIsWorse));
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
                    b.TopCandidates.Count > 0 ? b.TopCandidates[0].SuspicionScore : 0,
                    c.TopCandidates.Count > 0 ? c.TopCandidates[0].SuspicionScore : 0,
                    "score", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


