using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ArrayTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Array Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by type.array-population —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ArrayDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (ArrayTypeProfile p in r.TopArrayTypesBySize)
            {
                metrics.Add(new("array.type.bytes", p.ElementTypeName, p.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("array.type.count", p.ElementTypeName, p.Count, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


