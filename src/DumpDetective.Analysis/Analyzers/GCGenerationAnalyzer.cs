using System.Collections.Concurrent;
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
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "reading type statistics"));
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);
            return BuildDomainResult(heap, cache, cachedStats, progress);
        }

        private static GCGenerationDomainResult BuildDomainResult(ClrHeap heap, IHeapAnalysisCache cache, Dictionary<string, CachedTypeStatistics> typeStats, IProgress<AnalyzerProgressReport>? progress)
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
                progress?.Report(new(0, "scanning GC generations"));
                (int foundGen0Objects, int foundGen1Objects, ulong foundGen0Bytes, ulong foundGen1Bytes) =
                    RunParallelGenerationScan(heap, cache, generationProperty, getGenerationMethod, progress);

                gen0Objects = foundGen0Objects;
                gen1Objects = foundGen1Objects;
                gen0Bytes = foundGen0Bytes;
                gen1Bytes = foundGen1Bytes;

                // Subtract discovered gen0/gen1 from the gen2 fallback totals
                int adjObjects = gen0Objects + gen1Objects;
                gen2Objects = Math.Max(0, gen2Objects - adjObjects);
                ulong adjBytes = gen0Bytes + gen1Bytes;
                gen2Bytes = gen2Bytes >= adjBytes ? gen2Bytes - adjBytes : 0;
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

        // Counts gen0 and gen1 objects in parallel over either a flat in-memory HeapEntry[]
        // (cache path) or GC segments (no-cache path). Gen2 adjustment is done by the caller.
        private static (int gen0Objects, int gen1Objects, ulong gen0Bytes, ulong gen1Bytes)
            RunParallelGenerationScan(
                ClrHeap heap,
                IHeapAnalysisCache cache,
                PropertyInfo? generationProperty,
                MethodInfo? getGenerationMethod,
                IProgress<AnalyzerProgressReport>? progress)
        {
            int gen0Objects = 0;
            int gen1Objects = 0;
            ulong gen0Bytes = 0;
            ulong gen1Bytes = 0;
            long scanned = 0;
            const long progressInterval = 50_000;

            void ProcessEntry(ulong address, ulong size)
            {
                long s = Interlocked.Increment(ref scanned);
                if (s % progressInterval == 0)
                    progress?.Report(new(s, "scanning GC generations"));

                if (address == 0 || size >= LohThresholdBytes)
                    return;

                int generation = ResolveGeneration(heap, address, generationProperty, getGenerationMethod);

                if (generation == 0)
                {
                    Interlocked.Increment(ref gen0Objects);
                    Interlocked.Add(ref gen0Bytes, size);
                }
                else if (generation == 1)
                {
                    Interlocked.Increment(ref gen1Objects);
                    Interlocked.Add(ref gen1Bytes, size);
                }
            }

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                if (heapIdx.StorageKind == HeapIndexStorageKind.Memory && heapIdx.InMemoryEntries is { } entries)
                {
                    Parallel.ForEach(entries, entry => ProcessEntry(entry.Address, entry.Size));
                }
                else
                {
                    foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                        ProcessEntry(entry.Address, entry.Size);
                }
            }
            else
            {
                Parallel.ForEach(heap.Segments, segment =>
                {
                    foreach (ClrObject obj in segment.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type is null)
                            continue;
                        ProcessEntry(obj.Address, obj.Size);
                    }
                });
            }

            return (gen0Objects, gen1Objects, gen0Bytes, gen1Bytes);
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


    }
}
