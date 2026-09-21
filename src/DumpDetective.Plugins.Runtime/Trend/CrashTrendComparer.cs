using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class CrashTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Crash Analysis";

        // Headline metrics (exceptions.total, exceptions.active) retired onto ObservationTrendDiffer,
        // backed by runtime.crash-population — see ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CrashDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();
            foreach (var kv in r.ExceptionTypeCounts)
                metrics.Add(new("crash.exception.type", kv.Key, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CrashDomainResult b || current is not CrashDomainResult c) return [];
            var deltas = new List<MetricDelta>();
            foreach (var kv in c.ExceptionTypeCounts)
            {
                b.ExceptionTypeCounts.TryGetValue(kv.Key, out int baseCount);
                deltas.Add(MetricDeltaHelper.Compute("crash.exception.type", kv.Key, baseCount, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }
}


