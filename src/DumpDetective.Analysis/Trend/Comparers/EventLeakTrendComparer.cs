using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class EventLeakTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Event Leak Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not EventLeakDomainResult r) return [];
            return
            [
                new("event.leak.instances", null, r.TotalEventLeakInstances, "events", MetricTrendDirection.HigherIsWorse),
                new("event.total.subscribers", null, r.TotalSubscribers, "subscribers", MetricTrendDirection.HigherIsWorse),
                new("event.static.leaks", null, r.StaticEventLeakCount, "events", MetricTrendDirection.HigherIsWorse),
                new("event.instance.leaks", null, r.InstanceEventLeakCount, "events", MetricTrendDirection.HigherIsWorse),
                new("event.publisher.instances", null, r.TotalPublisherInstances, "publishers", MetricTrendDirection.Neutral)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not EventLeakDomainResult b || current is not EventLeakDomainResult c) return [];

            // Scoring formula changed between versions (design §9) — a severity/subscriber
            // delta computed across the boundary would be comparing apples to oranges, so
            // refuse to diff and emit a single informational note instead.
            if (b.ScoringVersion != c.ScoringVersion)
            {
                return
                [
                    new MetricDelta("event.leak.scoring_version_mismatch", null,
                        b.ScoringVersion, c.ScoringVersion, c.ScoringVersion - b.ScoringVersion,
                        null, "version", MetricTrendDirection.Neutral)
                ];
            }

            return
            [
                MetricDeltaHelper.Compute("event.leak.instances", null, b.TotalEventLeakInstances, c.TotalEventLeakInstances, "events", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.total.subscribers", null, b.TotalSubscribers, c.TotalSubscribers, "subscribers", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.static.leaks", null, b.StaticEventLeakCount, c.StaticEventLeakCount, "events", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.instance.leaks", null, b.InstanceEventLeakCount, c.InstanceEventLeakCount, "events", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.publisher.instances", null, b.TotalPublisherInstances, c.TotalPublisherInstances, "publishers", MetricTrendDirection.Neutral)
            ];
        }
    }
}


