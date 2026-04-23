using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers
{
    public class GCGenerationAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85000;

        // OPT-#3: Resolve reflection members once at class initialization instead of per-call inside
        // BuildDomainResult, which hit internal type-cache locks on every Analyze() invocation.
        private static readonly PropertyInfo? s_generationProperty =
            typeof(ClrObject).GetProperty("Generation");
        private static readonly MethodInfo? s_getGenerationMethod =
            typeof(ClrHeap).GetMethod("GetGeneration", [typeof(ulong)]);

        public string Name => "GC Generation Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzerExecutionResult executionResult = Analyze(context.Heap, context.Cache);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);

            return new AnalyzerExecutionResult(
                [CreateFinding(cachedStats)],
                BuildDomainResult(heap, cache, cachedStats));
        }

        private static GCGenerationDomainResult BuildDomainResult(ClrHeap heap, IHeapAnalysisCache cache, Dictionary<string, CachedTypeStatistics> typeStats)
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

            PropertyInfo? generationProperty = s_generationProperty;
            MethodInfo? getGenerationMethod = s_getGenerationMethod;

            try
            {
                foreach (HeapEntry entry in EnumerateGenerationEntries(heap, cache))
                {
                    ulong objectAddress = entry.Address;
                    if (objectAddress == 0)
                        continue;

                    ulong size = entry.Size;
                    if (size >= LohThresholdBytes)
                        continue;

                    int generation = ResolveGeneration(heap, objectAddress, generationProperty, getGenerationMethod);

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

        private static IEnumerable<HeapEntry> EnumerateGenerationEntries(ClrHeap heap, IHeapAnalysisCache cache)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    yield return entry;

                yield break;
            }

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong methodTable = obj.Type.MethodTable;
                if (methodTable == 0)
                    continue;

                yield return new HeapEntry(obj.Address, methodTable, obj.Size);
            }
        }

        private static int ResolveGeneration(ClrHeap heap, ulong objectAddress, PropertyInfo? generationProperty, MethodInfo? getGenerationMethod)
        {
            if (objectAddress == 0)
                return 2;

            try
            {
                if (generationProperty != null)
                {
                    ClrObject obj = heap.GetObject(objectAddress);
                    if (!obj.IsValid)
                        return 2;

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
                    object? value = getGenerationMethod.Invoke(heap, [objectAddress]);
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

        private static InsightFinding CreateFinding(Dictionary<string, CachedTypeStatistics> typeStats)
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


