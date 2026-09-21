using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class FinalizableObjectTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Finalizable Object Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by
        // gc.finalizable-composition/finalizer.queue-population — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not FinalizableObjectDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (TypeGenerationProfile p in r.TopFinalizableTypesByGen2Count)
                metrics.Add(new("finalizable.type.gen2.count", p.TypeName, p.Gen2Count, "objects", MetricTrendDirection.HigherIsWorse));
            foreach (FinalizerQueueEntry e in r.TopQueueEntriesByRetainedSize)
                metrics.Add(new("finalizable.queue.type.retained.bytes", e.TypeName, e.EstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


