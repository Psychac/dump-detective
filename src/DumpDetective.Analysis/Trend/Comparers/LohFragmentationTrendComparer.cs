using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class LohFragmentationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "LOH & POH Fragmentation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not LohFragmentationDomainResult r) return [];
            return
            [
                new("loh.fragmentation.percent", null, r.FragmentationPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("loh.free.bytes", null, r.FreeBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("loh.total.bytes", null, r.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("loh.largest.free.block", null, r.LargestFreeBlock, "bytes", MetricTrendDirection.LowerIsWorse),
                new("loh.segment.count", null, r.SegmentCount, "segments", MetricTrendDirection.Neutral)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not LohFragmentationDomainResult b || current is not LohFragmentationDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("loh.fragmentation.percent", null, b.FragmentationPercent, c.FragmentationPercent, "%",        MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.free.bytes",            null, b.FreeBytes,             c.FreeBytes,             "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.total.bytes",           null, b.TotalBytes,            c.TotalBytes,            "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.largest.free.block",    null, b.LargestFreeBlock,      c.LargestFreeBlock,      "bytes",   MetricTrendDirection.LowerIsWorse),
                MetricDeltaHelper.Compute("loh.segment.count",         null, b.SegmentCount,          c.SegmentCount,          "segments",MetricTrendDirection.Neutral)
            ];
        }
    }
}


