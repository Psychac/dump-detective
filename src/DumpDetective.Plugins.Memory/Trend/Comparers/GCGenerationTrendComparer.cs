using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class GCGenerationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Generation Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by gc.generation-composition
        // — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCGenerationDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (TypeSnapshot t in r.TopLohTypes)
            {
                metrics.Add(new("gc.loh.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("gc.loh.type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


