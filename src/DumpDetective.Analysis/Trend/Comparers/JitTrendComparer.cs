using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class JitTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "JIT Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not JitDomainResult r) return [];
            return
            [
                new("jit.heap.bytes",         null, r.TotalJitHeapBytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                new("jit.manager.count",      null, r.JitManagerCount,       "count",   MetricTrendDirection.Neutral),
                new("jit.active.methods",     null, r.ActiveMethodsOnStacks, "methods", MetricTrendDirection.Neutral),
                new("jit.tiered.count",       null, r.TieredMethodCount,     "methods", MetricTrendDirection.Neutral),
                new("jit.unmanaged.frames",   null, r.UnmanagedFrameCount,   "frames",  MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not JitDomainResult b || current is not JitDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("jit.heap.bytes",       null, b.TotalJitHeapBytes,     c.TotalJitHeapBytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("jit.manager.count",    null, b.JitManagerCount,       c.JitManagerCount,       "count",   MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("jit.active.methods",   null, b.ActiveMethodsOnStacks, c.ActiveMethodsOnStacks, "methods", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("jit.tiered.count",     null, b.TieredMethodCount,     c.TieredMethodCount,     "methods", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("jit.unmanaged.frames", null, b.UnmanagedFrameCount,   c.UnmanagedFrameCount,   "frames",  MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


