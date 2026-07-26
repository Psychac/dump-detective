using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ModuleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Module Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ModuleDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("modules.total", null, r.TotalModules, "modules", MetricTrendDirection.Neutral),
                new("modules.dynamic", null, r.DynamicModules, "modules", MetricTrendDirection.Neutral),
                new("modules.conflicts", null, r.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse),
                new("appdomain.count", null, r.TotalDomains, "domains", MetricTrendDirection.Neutral),
                new("appdomain.dynamic.modules", null, r.TotalDynamicModules, "modules", MetricTrendDirection.HigherIsWorse),
            };

            foreach (ModuleHeapStats m in r.TopModulesByHeapMemory ?? [])
            {
                metrics.Add(new("modules.heap.bytes", m.ModuleName, m.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("modules.heap.objects", m.ModuleName, m.ObjectCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            foreach (ModuleTypeCountEntry entry in r.TopModulesByTypeCount ?? [])
            {
                metrics.Add(new("appdomain.module.type.count", entry.ModuleName, entry.TypeCount, "types", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("appdomain.module.bytes", entry.ModuleName, entry.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ModuleDomainResult b || current is not ModuleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("modules.total", null, b.TotalModules, c.TotalModules, "modules", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("modules.conflicts", null, b.VersionConflictGroups, c.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("appdomain.count", null, b.TotalDomains, c.TotalDomains, "domains", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("appdomain.dynamic.modules", null, b.TotalDynamicModules, c.TotalDynamicModules, "modules", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


