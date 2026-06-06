using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AppDomainTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "AppDomain Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AppDomainDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("appdomain.count",          null, r.TotalDomains,        "domains", MetricTrendDirection.Neutral),
                new("appdomain.dynamic.modules", null, r.TotalDynamicModules, "modules", MetricTrendDirection.HigherIsWorse),
            };

            foreach (ModuleTypeCountEntry entry in r.TopModulesByTypeCount)
            {
                metrics.Add(new("appdomain.module.type.count", entry.ModuleName, entry.TypeCount, "types", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("appdomain.module.bytes", entry.ModuleName, entry.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AppDomainDomainResult b || current is not AppDomainDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("appdomain.count",           null, b.TotalDomains,        c.TotalDomains,        "domains", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("appdomain.dynamic.modules", null, b.TotalDynamicModules, c.TotalDynamicModules, "modules", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


