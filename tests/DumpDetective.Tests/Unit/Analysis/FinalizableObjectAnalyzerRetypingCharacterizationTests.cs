using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// covers what can be reliably controlled -- the population-sweep filter/sort arithmetic (synthetic
/// <c>TypeAggregates</c> keyed to real method tables, same shape earlier batches used) and the
/// no-index fallback gate. Queue-analysis correctness (disposable/critical-finalizer classification,
/// retained bytes, root paths) is NOT asserted here against a specific expected count: a live
/// .NET test-host process's actual finalizer queue content is inherently transient (GC-timing
/// dependent, unlike the static type/segment/root data prior batches' characterization tests relied
/// on) -- only internal-consistency invariants are checked live, with real behavioral verification
/// against the reference dump's genuinely populated finalizer queue left to
/// <see cref="FinalizableObjectAnalyzerRealDumpTests"/>.
/// </summary>
public sealed class FinalizableObjectAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_PopulationSweep_FiltersByIsFinalizableTypeAndSortsByGen2Count()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong[] methodTables = FindThreeDistinctMethodTables(heap);
            (ulong mtLowGen2, ulong mtHighGen2, ulong mtNonFinalizable) = (methodTables[0], methodTables[1], methodTables[2]);
            string nameLowGen2 = heap.GetTypeByMethodTable(mtLowGen2)!.Name!;
            string nameHighGen2 = heap.GetTypeByMethodTable(mtHighGen2)!.Name!;
            string nameNonFinalizable = heap.GetTypeByMethodTable(mtNonFinalizable)!.Name!;

            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [mtLowGen2] = new TypeAggregateIndexEntry(mtLowGen2, ModuleId: 0, Count: 5, TotalSize: 500, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 3, Gen1Count: 0, Gen2Count: 2, Flags: TypeAggregateFlags.IsFinalizableType),
                [mtHighGen2] = new TypeAggregateIndexEntry(mtHighGen2, ModuleId: 0, Count: 10, TotalSize: 1000, LohCount: 1, LohSize: 200, SampleAddress: 0, Gen0Count: 0, Gen1Count: 0, Gen2Count: 10, Flags: TypeAggregateFlags.IsFinalizableType),
                [mtNonFinalizable] = new TypeAggregateIndexEntry(mtNonFinalizable, ModuleId: 0, Count: 20, TotalSize: 2000, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 20, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 35,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates, TypeShapeCache: null));

            RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

            using FinalizableObjectAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (FinalizableObjectDomainResult)adapter.LastResult!;

            result.TotalFinalizableObjects.Should().Be(15); // 5 + 10, excludes the non-finalizable type
            result.TotalFinalizableBytes.Should().Be(1500);
            result.Gen2Count.Should().Be(12);
            result.TopFinalizableTypesByGen2Count.Should().HaveCount(2);
            result.TopFinalizableTypesByGen2Count[0].TypeName.Should().Be(nameHighGen2, "sorted by Gen2Count descending");
            result.TopFinalizableTypesByGen2Count[1].TypeName.Should().Be(nameLowGen2);
            result.TopFinalizableTypesByGen2Count.Should().NotContain(t => t.TypeName == nameNonFinalizable);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_NoHeapIndex_StillPopulatesFinalizableTypesFromLiveHeapFallback()
    {
        // heap.types' no-index fallback (StatisticsCache's full-heap-walk branch, extended this
        // batch to carry IsFinalizableType/Gen0-2Count) means this analyzer needs no fallback logic
        // of its own -- a live process heap always has at least some finalizable objects (e.g.
        // System.Threading.Thread, safe handles) for this to find without any injected index at all.
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

            using FinalizableObjectAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (FinalizableObjectDomainResult)adapter.LastResult!;

            result.TotalFinalizableObjects.Should().BeGreaterThan(0, "a live .NET process always has some finalizable objects on its heap");
            result.TotalFinalizableBytes.Should().BeGreaterThan(0UL);
            result.FinalizerQueueCount.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_QueueAnalysis_ProducesInternallyConsistentResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

            using FinalizableObjectAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (FinalizableObjectDomainResult)adapter.LastResult!;

            int classifiedByType = 0;
            foreach (var t in result.TopQueueTypesByCount)
                classifiedByType += t.QueueCount;
            classifiedByType.Should().BeLessThanOrEqualTo(result.FinalizerQueueCount);

            for (int i = 1; i < result.TopQueueEntriesByRetainedSize.Count; i++)
                result.TopQueueEntriesByRetainedSize[i].EstimatedRetainedBytes.Should().BeLessThanOrEqualTo(result.TopQueueEntriesByRetainedSize[i - 1].EstimatedRetainedBytes);

            foreach (var e in result.TopQueueEntriesByRetainedSize)
                e.Generation.Should().BeInRange(-1, 3);

            result.CriticalFinalizerQueueCount.Should().BeLessThanOrEqualTo(result.FinalizerQueueCount);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong[] FindThreeDistinctMethodTables(ClrHeap heap)
    {
        var found = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;
            ulong mt = obj.Type.MethodTable;
            if (seen.Add(mt))
            {
                found.Add(mt);
                if (found.Count == 3) break;
            }
        }
        return found.ToArray();
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult index)
    {
        var field = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(cache, index);
    }
}
