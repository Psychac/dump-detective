using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class FinalizableObjectTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Finalizable Object Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not FinalizableObjectDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("finalizable.total",            null, r.TotalFinalizableObjects, "objects", MetricTrendDirection.HigherIsWorse),
                new("finalizable.total.bytes",      null, r.TotalFinalizableBytes,   "bytes",   MetricTrendDirection.HigherIsWorse),
                new("finalizable.gen2.count",       null, r.Gen2Count,               "objects", MetricTrendDirection.HigherIsWorse),
                new("finalizable.queue.count",      null, r.FinalizerQueueCount,     "objects", MetricTrendDirection.HigherIsWorse),
                new("finalizable.queue.retained",   null, r.FinalizerQueueRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
            };

            foreach (TypeGenerationProfile p in r.TopFinalizableTypesByGen2Count)
                metrics.Add(new("finalizable.type.gen2.count", p.TypeName, p.Gen2Count, "objects", MetricTrendDirection.HigherIsWorse));
            foreach (FinalizerQueueEntry e in r.TopQueueEntriesByRetainedSize)
                metrics.Add(new("finalizable.queue.type.retained.bytes", e.TypeName, e.EstimatedRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not FinalizableObjectDomainResult b || current is not FinalizableObjectDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("finalizable.total",          null, b.TotalFinalizableObjects,    c.TotalFinalizableObjects,    "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("finalizable.total.bytes",    null, b.TotalFinalizableBytes,      c.TotalFinalizableBytes,      "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("finalizable.gen2.count",     null, b.Gen2Count,                  c.Gen2Count,                  "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("finalizable.queue.count",    null, b.FinalizerQueueCount,        c.FinalizerQueueCount,        "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("finalizable.queue.retained", null, b.FinalizerQueueRetainedBytes, c.FinalizerQueueRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


