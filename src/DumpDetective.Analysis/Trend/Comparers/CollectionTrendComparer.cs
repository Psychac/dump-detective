using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class CollectionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Collection Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CollectionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("collection.total", null, r.TotalCollections, "collections", MetricTrendDirection.Neutral),
                new("collection.wasted.bytes", null, r.TotalWastedMemory, "bytes", MetricTrendDirection.HigherIsWorse),
                new("collection.wasteful.count", null, r.WastefulCollectionCount, "collections", MetricTrendDirection.HigherIsWorse)
            };

            foreach (var kv in r.WasteCountsByKind ?? new Dictionary<CollectionKind, int>())
                metrics.Add(new("collection.waste.kind.count", kv.Key.ToString(), kv.Value, "collections", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CollectionDomainResult b || current is not CollectionDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("collection.wasted.bytes", null, b.TotalWastedMemory, c.TotalWastedMemory, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("collection.wasteful.count", null, b.WastefulCollectionCount, c.WastefulCollectionCount, "collections", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


