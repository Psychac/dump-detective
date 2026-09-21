using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ModuleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Module Analysis";

        // Headline metrics (modules.total/.dynamic/.conflicts, appdomain.count/.dynamic.modules)
        // retired onto ObservationTrendDiffer, backed by runtime.module-population — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket A.
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ModuleDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>();

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

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current) => [];
    }
}


