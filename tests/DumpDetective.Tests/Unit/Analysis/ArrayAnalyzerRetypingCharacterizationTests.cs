using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="ArrayAnalyzer"/> had no pre-existing test coverage at all before this retyping. Proves
/// the new <c>dump.array-elements</c> capability (<see cref="DumpDetective.Sdk.Analysis.IDumpArrayShapeQuery"/>)
/// against a real array on a self-attached live process heap — array contents, like field values,
/// have no shape that can be hand-crafted via reflection injection; only the surrounding
/// <c>TypeAggregates</c> population data is synthetic.
/// </summary>
public sealed class ArrayAnalyzerRetypingCharacterizationTests
{
    private readonly ITestOutputHelper _output;

    public ArrayAnalyzerRetypingCharacterizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Large and 60% zero-filled (comfortably above the analyzer's own 0.5 sparse-ratio threshold) --
    // sized well beyond any incidental int[] the runtime/test host itself might have allocated, so
    // "largest live Int32[] instance" below reliably identifies this one, not a collision.
    private const int ArrayLength = 200_000;
    private const int ZeroCount = 120_000;
    private static readonly int[] s_sparseArray = BuildSparseArray(ArrayLength, ZeroCount);

    private static int[] BuildSparseArray(int length, int zeroCount)
    {
        var arr = new int[length];
        for (int i = zeroCount; i < length; i++)
            arr[i] = i + 1; // guaranteed non-zero
        return arr;
    }

    [Fact]
    public async Task AnalyzeAsync_RealSparseArray_ComputesExactNullOrZeroDensity()
    {
        // s_sparseArray is never otherwise read in this method -- with "beforefieldinit" semantics
        // the JIT can defer (or never trigger) its static initializer until some member of this
        // type is actually touched, which could be well after the heap scan below runs. Force it
        // here so the array is guaranteed live and allocated before CreateSnapshotAndAttach.
        int forceInit = s_sparseArray.Length;

        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            forceInit.Should().Be(ArrayLength);
            ulong mt = FindMethodTableByTypeNameSuffix(heap, "Int32[]");
            ulong sampleAddress = FindAddressForMethodTable(heap, mt);

            ulong estimatedSize = (ulong)(ArrayLength * sizeof(int)) + 32; // header + length + data, approx
            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [mt] = new TypeAggregateIndexEntry(
                    mt, ModuleId: 0, Count: 1, TotalSize: estimatedSize, LohCount: 1, LohSize: estimatedSize,
                    SampleAddress: sampleAddress, Gen0Count: 0, Gen1Count: 0, Gen2Count: 0,
                    Flags: TypeAggregateFlags.IsArrayType),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 1,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates));

            AnalysisContext context = new()
            {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions { ArrayAnalysis = new ArrayAnalysisOptions { SparseSampleMinLength = 10 } },
            };

            using ArrayAnalyzerLegacyAdapter adapter = new();
            var result = (ArrayDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

            _output.WriteLine($"mt=0x{mt:x} sampleAddress=0x{sampleAddress:x}");
            _output.WriteLine($"TotalArrayObjects={result.TotalArrayObjects} TotalArrayBytes={result.TotalArrayBytes}");
            foreach (var t in result.TopArrayTypesBySize)
                _output.WriteLine($"  ArrayType: {t.ElementTypeName} rank={t.Rank} count={t.Count} bytes={t.TotalBytes} multiDim={t.IsMultiDimensional}");
            foreach (var la in result.TopLargeArrays)
                _output.WriteLine($"  LargeArray: 0x{la.Address:x} {la.ElementTypeName} len={la.Length} rank={la.Rank} size={la.Size}");
            foreach (var sa in result.TopSparseArrays)
                _output.WriteLine($"  SparseArray: 0x{sa.Address:x} {sa.ElementTypeName} len={sa.Length} nullOrZero={sa.NullOrZeroCount}");

            result.TotalArrayObjects.Should().Be(1);
            result.TopSparseArrays.Should().ContainSingle(a => a.Address == sampleAddress);
            SparseArrayEntry sparse = result.TopSparseArrays[0];
            sparse.Length.Should().Be(ArrayLength);
            sparse.NullOrZeroCount.Should().Be(ZeroCount);
            sparse.SparseRatio.Should().BeApproximately((double)ZeroCount / ArrayLength, 0.001);
            sparse.WastedBytes.Should().Be((ulong)ZeroCount * sizeof(int));
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
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using ArrayAnalyzerLegacyAdapter adapter = new();
            var result = (ArrayDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

            result.TotalArrayObjects.Should().Be(0);
            result.TopArrayTypesBySize.Should().BeEmpty();
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong FindMethodTableByTypeNameSuffix(ClrHeap heap, string suffix)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Type?.Name is string name && name.EndsWith(suffix, StringComparison.Ordinal))
                return obj.Type.MethodTable;
        }
        throw new InvalidOperationException($"No live object of type ending in '{suffix}' found on the test process heap.");
    }

    private static ulong FindAddressForMethodTable(ClrHeap heap, ulong methodTable)
    {
        // Force the sparse array to be reachable/findable: re-enumerate and match by MT, picking
        // the largest instance (our 100-element array should dominate any incidental small
        // Int32[] the runtime itself allocates).
        ulong best = 0;
        ulong bestSize = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Type?.MethodTable == methodTable && obj.Size > bestSize)
            {
                best = obj.Address;
                bestSize = obj.Size;
            }
        }
        if (best == 0)
            throw new InvalidOperationException("Could not re-find the sparse array instance on the heap.");
        return best;
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult index)
    {
        var field = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(cache, index);
    }
}
