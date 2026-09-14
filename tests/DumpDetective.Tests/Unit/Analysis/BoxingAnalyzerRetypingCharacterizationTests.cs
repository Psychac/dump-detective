using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using System.Linq;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// cross-checks the retyped <see cref="BoxingAnalyzer"/>'s classification arithmetic against
/// synthetic <c>TypeAggregates</c> entries keyed to real method tables from a self-attached live
/// process heap (type-shape data -- <c>IsEnum</c>/<c>StaticSize</c>/<c>HasIEquatableInterface</c>/
/// <c>DeclaredFieldByteSize</c> -- can't be hand-crafted via reflection injection any more than
/// segment/root/shape data in earlier batches could; each needs a real <see cref="ClrHeap.GetTypeByMethodTable"/>
/// lookup).
/// </summary>
public sealed class BoxingAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_ClassifiesBoxedValueTypes()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong[] methodTables = FindDistinctBoxedValueTypeMethodTables(heap, 2);
            methodTables.Should().NotBeEmpty("a live .NET test-host process always has some boxed value types on its heap");

            ulong mt = methodTables[0];
            ClrType clrType = heap.GetTypeByMethodTable(mt)!;
            string typeName = clrType.Name!;

            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [mt] = new TypeAggregateIndexEntry(mt, ModuleId: 0, Count: 7, TotalSize: 700, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 3, Gen1Count: 0, Gen2Count: 4, Flags: TypeAggregateFlags.None),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 7,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates, TypeShapeCache: null));

            RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

            using BoxingAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (BoxingDomainResult)adapter.LastResult!;

            result.TotalBoxedObjects.Should().Be(7);
            result.TotalBoxedBytes.Should().Be(700);
            result.TopBoxedTypes.Should().ContainSingle(t => t.ValueTypeName == typeName);

            BoxedTypeEntry entry = result.TopBoxedTypes.Single(t => t.ValueTypeName == typeName);
            entry.IsEnum.Should().Be(clrType.IsEnum);
            entry.Gen0Count.Should().Be(3);
            entry.Gen2Count.Should().Be(4);
            entry.Gen2Fraction.Should().BeApproximately(4.0 / 7.0, 0.0001);

            bool expectedHasIEquatable = SafeHasIEquatable(clrType);
            entry.HasIEquatable.Should().Be(expectedHasIEquatable);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_OversizedValueType_IsReportedWhenThresholdExceeded()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong[] methodTables = FindDistinctBoxedValueTypeMethodTables(heap, 1);
            methodTables.Should().NotBeEmpty("a live .NET test-host process always has some boxed value types on its heap");

            ulong mt = methodTables[0];
            ClrType clrType = heap.GetTypeByMethodTable(mt)!;

            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [mt] = new TypeAggregateIndexEntry(mt, ModuleId: 0, Count: 2, TotalSize: 200, LohCount: 0, LohSize: 0, SampleAddress: 0, Gen0Count: 2, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 2,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates, TypeShapeCache: null));

            // Threshold of -1 forces every value type to count as "oversized", regardless of its
            // real StaticSize -- isolates the threshold-comparison logic from needing a specific
            // real type's exact size.
            BoxingAnalysisOptions boxingAnalysisOptions = new() { OversizedThresholdBytes = -1 };
            RuntimeAnalysisContext contextRuntimeCtx = new() {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions { BoxingAnalysis = boxingAnalysisOptions },
            };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, boxingAnalysisOptions);

            using BoxingAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (BoxingDomainResult)adapter.LastResult!;

            result.OversizedValueTypeInstanceCount.Should().Be(2);
            result.TopOversizedTypes.Should().ContainSingle(t => t.TypeName == clrType.Name);
            result.TopOversizedTypes.Single().StaticSize.Should().Be(clrType.StaticSize);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_NoHeapIndex_ReturnsEmptyResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();

            RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

            using BoxingAnalyzer adapter = new();
            await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (BoxingDomainResult)adapter.LastResult!;

            result.TotalBoxedObjects.Should().Be(0);
            result.TopBoxedTypes.Should().BeEmpty();
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong[] FindDistinctBoxedValueTypeMethodTables(ClrHeap heap, int count)
    {
        var found = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || !obj.Type.IsValueType) continue;
            ulong mt = obj.Type.MethodTable;
            if (seen.Add(mt))
            {
                found.Add(mt);
                if (found.Count == count) break;
            }
        }
        return found.ToArray();
    }

    private static bool SafeHasIEquatable(ClrType type)
    {
        try
        {
            foreach (ClrInterface iface in type.EnumerateInterfaces())
            {
                if (iface.Name is not null && iface.Name.StartsWith("IEquatable", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult index)
    {
        var field = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(cache, index);
    }
}
