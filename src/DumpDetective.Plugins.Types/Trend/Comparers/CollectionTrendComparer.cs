using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class CollectionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Collection Analysis";

        // Headline metrics (total, wasted.bytes, wasteful.count) retired onto ObservationTrendDiffer,
        // backed by collection.waste-summary — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CollectionDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (var kv in r.WasteCountsByKind ?? new Dictionary<CollectionKind, int>())
                metrics.Add(new("collection.waste.kind.count", kv.Key.ToString(), kv.Value, "collections", MetricTrendDirection.HigherIsWorse));

            foreach (var kv in r.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>())
                metrics.Add(new("collection.waste.kind.bytes", kv.Key.ToString(), kv.Value, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CollectionDomainResult b || current is not CollectionDomainResult c) return [];

            var deltas = new List<MetricDelta>();

            // A kind present in only one snapshot still needs a delta — a kind that appeared or
            // vanished between runs is exactly the regression signal per-kind trending exists for.
            var baselineBytes = b.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>();
            var currentBytes = c.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>();
            var baselineCounts = b.WasteCountsByKind ?? new Dictionary<CollectionKind, int>();
            var currentCounts = c.WasteCountsByKind ?? new Dictionary<CollectionKind, int>();

            foreach (var kind in UnionOfKinds(baselineBytes.Keys, currentBytes.Keys))
            {
                baselineBytes.TryGetValue(kind, out ulong baseValue);
                currentBytes.TryGetValue(kind, out ulong currentValue);
                deltas.Add(MetricDeltaHelper.Compute("collection.waste.kind.bytes", kind.ToString(), baseValue, currentValue, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            foreach (var kind in UnionOfKinds(baselineCounts.Keys, currentCounts.Keys))
            {
                baselineCounts.TryGetValue(kind, out int baseValue);
                currentCounts.TryGetValue(kind, out int currentValue);
                deltas.Add(MetricDeltaHelper.Compute("collection.waste.kind.count", kind.ToString(), baseValue, currentValue, "collections", MetricTrendDirection.HigherIsWorse));
            }

            return deltas;
        }

        private static List<CollectionKind> UnionOfKinds(IEnumerable<CollectionKind> baseline, IEnumerable<CollectionKind> current)
        {
            var kinds = new SortedSet<CollectionKind>(baseline);
            foreach (var kind in current)
                kinds.Add(kind);
            return [.. kinds];
        }
    }
}


