using DumpDetective.Analysis.Analyzers.Types;
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
/// Characterization test for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="ObjectShapeAnalyzer"/> had no pre-existing unit test coverage at all before this
/// retyping — first real coverage, not a port. Cross-checks the retyped analyzer's classification
/// arithmetic against synthetic <c>TypeShapeCache</c>/<c>TypeAggregates</c> entries keyed to real
/// method tables from a self-attached live process heap (type-shape data can't be hand-crafted via
/// reflection injection any more than segment/root data in earlier batches could — <c>ClrType</c>
/// resolution for <c>BaseTypeChainDepth</c>/<c>InterfaceCount</c>/<c>IsValueType</c>/<c>IsArray</c>
/// needs a real <see cref="ClrHeap.GetTypeByMethodTable"/> lookup).
/// </summary>
public sealed class ObjectShapeAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_ClassifiesByReferenceFieldRatio()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong[] methodTables = FindThreeDistinctMethodTables(heap);
            (ulong mtRefHeavy, ulong mtValHeavy, ulong mtScalar) = (methodTables[0], methodTables[1], methodTables[2]);
            string nameRefHeavy = heap.GetTypeByMethodTable(mtRefHeavy)!.Name!;
            string nameValHeavy = heap.GetTypeByMethodTable(mtValHeavy)!.Name!;
            string nameScalar = heap.GetTypeByMethodTable(mtScalar)!.Name!;

            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [mtRefHeavy] = new TypeAggregateIndexEntry(mtRefHeavy, ModuleId: 0, Count: 10, TotalSize: 1000, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 10, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
                [mtValHeavy] = new TypeAggregateIndexEntry(mtValHeavy, ModuleId: 0, Count: 20, TotalSize: 2000, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 20, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
                [mtScalar] = new TypeAggregateIndexEntry(mtScalar, ModuleId: 0, Count: 5, TotalSize: 500, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 5, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
            };

            var shapes = new Dictionary<ulong, TypeShapeEntry>
            {
                [mtRefHeavy] = new TypeShapeEntry(refFields: 8, valFields: 1), // ratio 0.89 -> ReferenceHeavy
                [mtValHeavy] = new TypeShapeEntry(refFields: 1, valFields: 9), // ratio 0.10 -> ValueHeavy
                [mtScalar] = new TypeShapeEntry(refFields: 0, valFields: 0),   // TotalFields 0 -> Scalar
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 35,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates, TypeShapeCache: shapes));

            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using ObjectShapeAnalyzerLegacyAdapter adapter = new();
            var result = (ObjectShapeAnalyzerDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

            result.TotalTypesAnalyzed.Should().Be(3);
            result.TopReferenceHeavyTypes.Should().ContainSingle(t => t.TypeName == nameRefHeavy);
            result.TopReferenceHeavyTypes[0].Category.Should().Be(ObjectShapeCategory.ReferenceHeavy);
            result.TopValueHeavyTypes.Should().ContainSingle(t => t.TypeName == nameValHeavy);
            result.TopBalancedTypes.Should().BeEmpty();

            // Scalar-category types (TotalFields == 0) aren't added to any of the three top-N
            // buckets by design -- matches the pre-retyping analyzer's own behavior exactly.
            result.TopReferenceHeavyTypes.Should().NotContain(t => t.TypeName == nameScalar);
            result.TopValueHeavyTypes.Should().NotContain(t => t.TypeName == nameScalar);
            result.TopBalancedTypes.Should().NotContain(t => t.TypeName == nameScalar);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_NoTypeShapeCache_ReturnsEmptyResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 0,
                Elapsed: TimeSpan.Zero, TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>(),
                TypeShapeCache: null));

            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using ObjectShapeAnalyzerLegacyAdapter adapter = new();
            var result = (ObjectShapeAnalyzerDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

            result.TotalTypesAnalyzed.Should().Be(0);
            result.TopReferenceHeavyTypes.Should().BeEmpty();
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
