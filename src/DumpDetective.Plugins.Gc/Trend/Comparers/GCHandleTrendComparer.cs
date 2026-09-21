using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCHandleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Handle Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by handle.population — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCHandleDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (NameCountEntry entry in r.HandlesByKind ?? [])
                metrics.Add(new("gchandle.kind.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopTargetTypes ?? [])
                metrics.Add(new("gchandle.target.type.count", entry.Name, entry.Count, "targets", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopPinnedTargetTypes ?? [])
                metrics.Add(new("gchandle.pinned.type.count", entry.Name, entry.Count, "targets", MetricTrendDirection.HigherIsWorse));
            foreach (NameBytesEntry entry in r.TopPinnedObjectsBySize ?? [])
                metrics.Add(new("gchandle.pinned.type.bytes", entry.Name, entry.Bytes, "bytes", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.DependentTopSourceTypes ?? [])
                metrics.Add(new("gchandle.dependent.source.type.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.DependentTopTargetTypes ?? [])
                metrics.Add(new("gchandle.dependent.target.type.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.DependentTopSourceTargetEdges ?? [])
                metrics.Add(new("gchandle.dependent.edge.type.count", entry.Name, entry.Count, "edges", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}
