using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCGenerationAnalyzer : IAnalyzer
    {
        public string Name => "GC Generation Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Heap, context.Cache);

        public AnalyzerExecutionResult Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);

            return new AnalyzerExecutionResult(
                [CreateFinding(cachedStats)],
                BuildDomainResult(cachedStats));
        }

        private static GCGenerationDomainResult BuildDomainResult(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong gen2Bytes = 0;
            ulong lohBytes = 0;
            int totalObjects = 0;
            int lohObjects = 0;
            foreach (var stat in typeStats.Values)
            {
                gen2Bytes += stat.TotalSize - stat.LohSize;
                lohBytes += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;
            }
            double lohPct = (gen2Bytes + lohBytes) == 0 ? 0 : lohBytes * 100.0 / (gen2Bytes + lohBytes);
            return new GCGenerationDomainResult(gen2Bytes, lohBytes, lohPct, totalObjects, lohObjects);
        }

        private static InsightFinding CreateFinding(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong total = 0;
            ulong loh = 0;
            foreach (var stat in typeStats.Values)
            {
                total += stat.TotalSize;
                loh += stat.LohSize;
            }

            double lohPct = total == 0 ? 0 : loh * 100.0 / total;
            return new InsightFinding(
                Analyzer: nameof(GCGenerationAnalyzer),
                Category: "GC",
                Severity: lohPct >= 35 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "GC generation footprint snapshot",
                Evidence: $"LOH memory share is {lohPct:F1}% of managed heap.",
                Recommendation: lohPct >= 35
                    ? "Inspect large object churn and promotion patterns."
                    : "Generation split appears within expected range for this dump.",
                Tags: ["gc", "generations", "loh"],
                MetricValue: lohPct,
                MetricUnit: "%");
        }
    }
}
