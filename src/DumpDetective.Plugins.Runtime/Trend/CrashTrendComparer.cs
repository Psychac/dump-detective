using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class CrashTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Crash Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CrashDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("crash.exceptions.total", null, r.TotalExceptions, "exceptions", MetricTrendDirection.HigherIsWorse),
                new("crash.exceptions.active", null, r.ActiveExceptions, "exceptions", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in r.ExceptionTypeCounts)
                metrics.Add(new("crash.exception.type", kv.Key, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CrashDomainResult b || current is not CrashDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("crash.exceptions.total", null, b.TotalExceptions, c.TotalExceptions, "exceptions", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("crash.exceptions.active", null, b.ActiveExceptions, c.ActiveExceptions, "exceptions", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in c.ExceptionTypeCounts)
            {
                b.ExceptionTypeCounts.TryGetValue(kv.Key, out int baseCount);
                deltas.Add(MetricDeltaHelper.Compute("crash.exception.type", kv.Key, baseCount, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }
}


