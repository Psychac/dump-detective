using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class WeakReferenceTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Weak Reference Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not WeakReferenceDomainResult r) return [];
            return
            [
                new("weakref.handles.total",       null, r.TotalWeakHandles,            "handles", MetricTrendDirection.HigherIsWorse),
                new("weakref.dead.ratio",          null, r.DeadTargetRatio,             "ratio",   MetricTrendDirection.HigherIsWorse),
                new("weakref.dead.count",          null, r.DeadWeakTargets,             "handles", MetricTrendDirection.HigherIsWorse),
                new("weakref.stale.wrappers",      null, r.StaleWrapperCount,           "objects", MetricTrendDirection.HigherIsWorse),
                new("weakref.objects.bytes",       null, r.WeakReferenceObjectBytes,    "bytes",   MetricTrendDirection.HigherIsWorse),
                new("weakref.dephandle.dead.keys", null, r.DependentHandleDeadKeyCount, "handles", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not WeakReferenceDomainResult b || current is not WeakReferenceDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("weakref.handles.total",       null, b.TotalWeakHandles,            c.TotalWeakHandles,            "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("weakref.dead.ratio",          null, b.DeadTargetRatio,             c.DeadTargetRatio,             "ratio",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("weakref.dead.count",          null, b.DeadWeakTargets,             c.DeadWeakTargets,             "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("weakref.stale.wrappers",      null, b.StaleWrapperCount,           c.StaleWrapperCount,           "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("weakref.objects.bytes",       null, b.WeakReferenceObjectBytes,    c.WeakReferenceObjectBytes,    "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("weakref.dephandle.dead.keys", null, b.DependentHandleDeadKeyCount, c.DependentHandleDeadKeyCount, "handles", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


