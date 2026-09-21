using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class StringTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "String Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by type.string-composition —
        // see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StringDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (NameCountEntry entry in r.TopDuplicateTypes ?? [])
                metrics.Add(new("string.duplicate.type.count", entry.Name, entry.Count, "objects", MetricTrendDirection.HigherIsWorse));

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


