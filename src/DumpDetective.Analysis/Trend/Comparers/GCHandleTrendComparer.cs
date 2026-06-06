using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCHandleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Handle Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCHandleDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("gchandle.total",              null, r.TotalHandles,          "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.strong",             null, r.StrongLikeHandles,     "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.pinned.targets",     null, r.PinnedHandleTargets,   "targets", MetricTrendDirection.HigherIsWorse),
                new("gchandle.pinned.bytes",       null, r.PinnedRetainedBytes,   "bytes",   MetricTrendDirection.HigherIsWorse)
            };

            foreach (NameCountEntry entry in r.HandlesByKind ?? [])
                metrics.Add(new("gchandle.kind.count", entry.Name, entry.Count, "handles", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopTargetTypes ?? [])
                metrics.Add(new("gchandle.target.type.count", entry.Name, entry.Count, "targets", MetricTrendDirection.HigherIsWorse));
            foreach (NameCountEntry entry in r.TopPinnedTargetTypes ?? [])
                metrics.Add(new("gchandle.pinned.type.count", entry.Name, entry.Count, "targets", MetricTrendDirection.HigherIsWorse));
            foreach (NameBytesEntry entry in r.TopPinnedObjectsBySize ?? [])
                metrics.Add(new("gchandle.pinned.type.bytes", entry.Name, entry.Bytes, "bytes", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCHandleDomainResult b || current is not GCHandleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gchandle.total",          null, b.TotalHandles,        c.TotalHandles,        "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.strong",         null, b.StrongLikeHandles,   c.StrongLikeHandles,   "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.pinned.targets", null, b.PinnedHandleTargets, c.PinnedHandleTargets, "targets", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.pinned.bytes",   null, b.PinnedRetainedBytes, c.PinnedRetainedBytes, "bytes",   MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


