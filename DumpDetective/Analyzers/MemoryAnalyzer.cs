using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85000;

        public string Name => "Memory Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Heap, context.Cache);

        public AnalyzerExecutionResult Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            return new AnalyzerExecutionResult(
                [CreateFinding(typeStats)],
                BuildDomainResult(typeStats));
        }

        private static MemoryDomainResult BuildDomainResult(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong totalMemory = 0;
            ulong totalLohMemory = 0;
            int totalObjects = 0;
            int lohObjects = 0;
            foreach (var stat in typeStats.Values)
            {
                totalMemory += stat.TotalSize;
                totalLohMemory += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;
            }

            double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;

            var bySize = new List<TypeStatistics>(typeStats.Values);
            bySize.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            var byCount = new List<TypeStatistics>(typeStats.Values);
            byCount.Sort((a, b) => b.Count.CompareTo(a.Count));

            static TypeSnapshot ToSnapshot(TypeStatistics s) =>
                new(s.TypeName, s.Count, s.TotalSize, s.LohSize);

            return new MemoryDomainResult(
                totalMemory,
                totalLohMemory,
                lohPct,
                totalObjects,
                lohObjects,
                LohThresholdBytes,
                typeStats.Count,
                bySize.Take(20).Select(ToSnapshot).ToList(),
                byCount.Take(20).Select(ToSnapshot).ToList());
        }

        private static InsightFinding CreateFinding(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong totalMemory = 0;
            ulong totalLohMemory = 0;
            foreach (var stat in typeStats.Values)
            {
                totalMemory += stat.TotalSize;
                totalLohMemory += stat.LohSize;
            }

            double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;
            FindingSeverity severity = lohPct >= 40 ? FindingSeverity.Warning : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(MemoryAnalyzer),
                Category: "Memory",
                Severity: severity,
                Title: "Heap composition overview",
                Evidence: $"{typeStats.Count:N0} unique types, {FormatHelper.FormatBytes(totalMemory)} total memory, LOH share {lohPct:F1}%.",
                Recommendation: lohPct >= 40
                    ? "Review large-object allocation patterns and retention lifetimes."
                    : "Use top types by size/count as primary triage anchors.",
                Tags: ["heap", "composition", "loh"],
                MetricValue: lohPct,
                MetricUnit: "%");
        }
    }
}
