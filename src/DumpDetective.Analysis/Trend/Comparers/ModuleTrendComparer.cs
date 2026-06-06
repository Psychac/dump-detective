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
                new("modules.conflicts", null, r.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse)
            };

            foreach (ModuleHeapStats m in r.TopModulesByHeapMemory ?? [])
            {
                metrics.Add(new("modules.heap.bytes", m.ModuleName, m.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("modules.heap.objects", m.ModuleName, m.ObjectCount, "objects", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ModuleDomainResult b || current is not ModuleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("modules.total", null, b.TotalModules, c.TotalModules, "modules", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("modules.conflicts", null, b.VersionConflictGroups, c.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }
}


