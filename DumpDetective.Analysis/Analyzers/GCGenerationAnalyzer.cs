using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    internal class GCGenerationAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85000;

        public string Name => "GC Generation Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Heap, context.Cache);

        public AnalyzerExecutionResult Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);

            return new AnalyzerExecutionResult(
                [CreateFinding(cachedStats)],
                BuildDomainResult(heap, cachedStats));
        }

        private static GCGenerationDomainResult BuildDomainResult(ClrHeap heap, Dictionary<string, TypeStatistics> typeStats)
        {
            ulong gen0Bytes = 0;
            ulong gen1Bytes = 0;
            ulong gen2Bytes = 0;
            ulong lohBytes = 0;
            int totalObjects = 0;
            int lohObjects = 0;
            int gen0Objects = 0;
            int gen1Objects = 0;
            int gen2Objects = 0;

            foreach (var stat in typeStats.Values)
            {
                lohBytes += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;

                int nonLohObjects = Math.Max(0, stat.Count - stat.LohCount);
                ulong nonLohBytes = stat.TotalSize - stat.LohSize;
                gen2Objects += nonLohObjects;
                gen2Bytes += nonLohBytes;
            }

            PropertyInfo? generationProperty = typeof(ClrObject).GetProperty("Generation");
            MethodInfo? getGenerationMethod = typeof(ClrHeap).GetMethod("GetGeneration", [typeof(ulong)]);

            try
            {
                foreach (ClrObject obj in heap.EnumerateObjects())
                {
                    if (!obj.IsValid)
                        continue;

                    ulong size = obj.Size;
                    if (size >= LohThresholdBytes)
                        continue;

                    int generation = ResolveGeneration(heap, obj, generationProperty, getGenerationMethod);

                    if (generation == 0)
                    {
                        gen0Objects++;
                        gen0Bytes += size;
                        gen2Objects = Math.Max(0, gen2Objects - 1);
                        gen2Bytes = gen2Bytes >= size ? gen2Bytes - size : 0;
                    }
                    else if (generation == 1)
                    {
                        gen1Objects++;
                        gen1Bytes += size;
                        gen2Objects = Math.Max(0, gen2Objects - 1);
                        gen2Bytes = gen2Bytes >= size ? gen2Bytes - size : 0;
                    }
                }
            }
            catch
            {
                // If generation metadata scanning fails, keep fallback Gen2-centric split.
            }

            ulong totalManagedBytes = gen0Bytes + gen1Bytes + gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0 : lohBytes * 100.0 / totalManagedBytes;

            var topLohTypes = typeStats.Values
                .Where(s => s.LohCount > 0 && s.LohSize > 0)
                .OrderByDescending(s => s.LohSize)
                .Take(15)
                .Select(s => new TypeSnapshot(s.TypeName, s.LohCount, s.LohSize, s.LohSize))
                .ToList();

            return new GCGenerationDomainResult(
                gen0Bytes,
                gen0Objects,
                gen1Bytes,
                gen1Objects,
                gen2Bytes,
                gen2Objects,
                lohBytes,
                lohPct,
                totalObjects,
                lohObjects,
                topLohTypes);
        }

        private static int ResolveGeneration(ClrHeap heap, ClrObject obj, PropertyInfo? generationProperty, MethodInfo? getGenerationMethod)
        {
            try
            {
                if (generationProperty != null)
                {
                    object boxed = obj;
                    object? value = generationProperty.GetValue(boxed);
                    if (value is int gen)
                        return gen;
                }
            }
            catch
            {
                // try next strategy
            }

            try
            {
                if (getGenerationMethod != null)
                {
                    object? value = getGenerationMethod.Invoke(heap, [obj.Address]);
                    if (value is int gen)
                        return gen;
                }
            }
            catch
            {
                // fallback below
            }

            return 2;
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


