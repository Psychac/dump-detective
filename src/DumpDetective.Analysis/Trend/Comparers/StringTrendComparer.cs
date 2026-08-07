using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class StringTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "String Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StringDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("string.total", null, r.TotalStrings, "objects", MetricTrendDirection.HigherIsWorse),
                new("string.total.bytes", null, r.TotalStringMemoryBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.unique", null, r.SampledUniquePatterns, "objects", MetricTrendDirection.Neutral),
                new("string.duplicate.patterns", null, r.DuplicatePatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                new("string.duplicate.wasted.bytes", null, r.DuplicateWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.duplication.ratio", null, r.DuplicationRatio, "ratio", MetricTrendDirection.HigherIsWorse),
                new("string.loh.bytes", null, r.LohStringBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.pct.heap", null, r.PctOfManagedHeap, "%", MetricTrendDirection.HigherIsWorse),
                new("string.gen2.count", null, r.Gen2StringCount, "objects", MetricTrendDirection.HigherIsWorse),
            };

            foreach (NameCountEntry entry in r.TopDuplicateTypes ?? [])
                metrics.Add(new("string.duplicate.type.count", entry.Name, entry.Count, "objects", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not StringDomainResult b || current is not StringDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("string.total", null, b.TotalStrings, c.TotalStrings, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.total.bytes", null, b.TotalStringMemoryBytes, c.TotalStringMemoryBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.unique", null, b.SampledUniquePatterns, c.SampledUniquePatterns, "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("string.duplicate.patterns", null, b.DuplicatePatternCount, c.DuplicatePatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.duplicate.wasted.bytes", null, b.DuplicateWastedBytes, c.DuplicateWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.duplication.ratio", null, b.DuplicationRatio, c.DuplicationRatio, "ratio", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.loh.bytes", null, b.LohStringBytes, c.LohStringBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.pct.heap", null, b.PctOfManagedHeap, c.PctOfManagedHeap, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.gen2.count", null, b.Gen2StringCount, c.Gen2StringCount, "objects", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


