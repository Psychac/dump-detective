using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class HttpObjectTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "HTTP Object Analysis";

        // Headline metrics retired onto ObservationTrendDiffer, backed by
        // infra.http-object-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket D.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HttpObjectDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

            foreach (HttpObjectTypeSummary t in r.ByType)
            {
                metrics.Add(new("http.type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("http.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


