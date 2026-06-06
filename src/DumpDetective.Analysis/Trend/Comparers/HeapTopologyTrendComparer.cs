using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class HeapTopologyTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Heap Topology";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HeapTopologyDomainResult r) return [];
            return
            [
                new("segment.total", null, r.TotalSegments, "segments", MetricTrendDirection.Neutral),
                new("segment.committed.bytes", null, r.TotalCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.loh.bytes", null, r.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.loh.percent", null, r.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("segment.poh.bytes", null, r.PohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.poh.percent", null, r.PohPercent, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not HeapTopologyDomainResult b || current is not HeapTopologyDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("segment.total", null, b.TotalSegments, c.TotalSegments, "segments", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("segment.committed.bytes", null, b.TotalCommittedBytes, c.TotalCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.loh.bytes", null, b.LohBytes, c.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.loh.percent", null, b.LohPercent, c.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.poh.bytes", null, b.PohBytes, c.PohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.poh.percent", null, b.PohPercent, c.PohPercent, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


