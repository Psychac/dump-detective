using System.Buffers.Binary;
using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// P3-4: functional tests against fake/mocked handle data — exercises the real
/// <see cref="GCHandleAnalyzer"/> production code path (disk snapshot -&gt; ProcessHandle -&gt;
/// domain result) without a real dump, using the same disk-index-injection pattern established
/// by <c>AnalysisPipelineTests.InjectHeapIndex</c>. <c>heap</c> is intentionally null throughout:
/// fields that require a live <c>ClrHeap</c> (SOH/generation classification, retained bytes,
/// dependent-handle type resolution) are expected to come back at their zero/unresolved default —
/// that's the documented graceful-degradation contract, not a gap in these tests.
/// </summary>
public sealed class GCHandleAnalyzerFunctionalTests
{
    // ClrHandleKind byte values used by GCHandleAnalyzer's ((ClrHandleKind)kindByte).ToString() —
    // see Microsoft.Diagnostics.Runtime.ClrHandleKind.
    private const byte KindWeakShort = 0;
    private const byte KindWeakLong = 1;
    private const byte KindStrong = 2;
    private const byte KindPinned = 3;
    private const byte KindRefCounted = 5;
    private const byte KindDependent = 6;
    private const byte KindAsyncPinned = 7;

    [Fact]
    public void Analyze_CountsHandlesByKind_AndSplitsStrongVsWeak()
    {
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandles(
            (0x1000UL, 0xA000UL, KindStrong),
            (0x1100UL, 0xA000UL, KindWeakShort),
            (0x1200UL, 0xA000UL, KindWeakLong),
            (0x1300UL, 0xA000UL, KindPinned));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.TotalHandles.Should().Be(4);
        result.StrongLikeHandles.Should().Be(2); // Strong + Pinned (neither name contains "Weak")
        result.WeakLikeHandles.Should().Be(2);   // WeakShort + WeakLong
        result.HandlesByKind.Should().Contain(e => e.Name == "WeakShort" && e.Count == 1);
        result.HandlesByKind.Should().Contain(e => e.Name == "WeakLong" && e.Count == 1);
        result.HandlesByKind.Should().Contain(e => e.Name == "Pinned" && e.Count == 1);
        result.HandlesByKind.Should().Contain(e => e.Name == "Strong" && e.Count == 1);
    }

    [Fact]
    public void Analyze_TracksNullTargetHandlesPerKind_AndDoesNotCountThemAsPinnedTargets()
    {
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandles(
            (0UL, 0UL, KindPinned),
            (0x2000UL, 0xB000UL, KindPinned));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.TotalHandles.Should().Be(2);
        result.PinnedHandleTargets.Should().Be(1);
        result.NullTargetHandlesByKind.Should().ContainSingle(e => e.Name == "Pinned" && e.Count == 1);
    }

    [Fact]
    public void Analyze_CountsPinnedAndAsyncPinnedTargetTypes()
    {
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandles(
            (0x3000UL, 0xC000UL, KindPinned),
            (0x3100UL, 0xC000UL, KindPinned),
            (0x3200UL, 0xD000UL, KindAsyncPinned));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.PinnedHandleTargets.Should().Be(2);
        // heap == null -> ResolveTypeNameFromRecord falls back to a per-address pseudo type name
        // ("Object@0x...") instead of grouping by MethodTable, so each handle gets its own entry.
        result.TopPinnedTargetTypes!.Sum(e => e.Count).Should().Be(2);
        // heap == null -> ResolveSize returns 0, so no pinned-bytes accounting is expected here.
        result.PinnedRetainedBytes.Should().Be(0);
    }

    [Fact]
    public void Analyze_TracksRefCountedTargetTypeConcentration()
    {
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandles(
            (0x4000UL, 0xE000UL, KindRefCounted),
            (0x4100UL, 0xE000UL, KindRefCounted),
            (0x4200UL, 0xE000UL, KindRefCounted));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.RefCountedHandleCount.Should().Be(3);
        // heap == null -> per-address pseudo type names, no MethodTable-based grouping (see above).
        result.TopRefCountedTargetTypes!.Sum(e => e.Count).Should().Be(3);
    }

    [Fact]
    public void Analyze_WithHeapNull_DoesNotTrackDependentHandleTopology()
    {
        // Matches the pre-P3-3 live-enumeration pass's own limitation: dependent-handle
        // source/target type resolution requires a live heap, so heap == null means no
        // dependent-handle topology is tracked at all (not just "unresolved" — untracked).
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandles(
            (0x5000UL, 0xF000UL, KindDependent, 0x5FFFUL));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.DependentHandleCount.Should().Be(0);
    }

    [Fact]
    public void Analyze_ReadsV1DiskSnapshot_WithoutDependentTarget_ForBackwardCompatibility()
    {
        GCHandleAnalyzer analyzer = new();
        IHeapAnalysisCache cache = BuildCacheWithHandlesV1(
            (0x6000UL, 0x7000UL, KindStrong),
            (0x6100UL, 0x7100UL, KindPinned));

        var result = (GCHandleDomainResult)analyzer.Analyze(runtime: null!, heap: null, cache: cache);

        result.TotalHandles.Should().Be(2);
        result.PinnedHandleTargets.Should().Be(1);
    }

    private static IHeapAnalysisCache BuildCacheWithHandles(params (ulong Address, ulong MethodTable, byte Kind, ulong DependentTarget)[] records)
    {
        string containerPath = WriteHandlesContainer(records, version: 2);
        return InjectDiskHeapIndex(containerPath);
    }

    private static IHeapAnalysisCache BuildCacheWithHandles(params (ulong Address, ulong MethodTable, byte Kind)[] records) =>
        BuildCacheWithHandles(records.Select(r => (r.Address, r.MethodTable, r.Kind, DependentTarget: 0UL)).ToArray());

    private static IHeapAnalysisCache BuildCacheWithHandlesV1(params (ulong Address, ulong MethodTable, byte Kind)[] records)
    {
        string containerPath = WriteHandlesContainer(
            records.Select(r => (r.Address, r.MethodTable, r.Kind, DependentTarget: 0UL)).ToArray(),
            version: 1);
        return InjectDiskHeapIndex(containerPath);
    }

    private static string WriteHandlesContainer((ulong Address, ulong MethodTable, byte Kind, ulong DependentTarget)[] records, int version)
    {
        string containerPath = Path.Combine(Path.GetTempPath(), "gchandle-functional-tests", $"{Guid.NewGuid()}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(containerPath)!);

        const int Magic = 0x53534448; // "HDSS" Handle Snapshot — must match HandleSnapshotWriter.
        int recordSize = version >= 2 ? 28 : 20;

        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.Handles);

        var header = new IndexHeader(Magic, version, records.Length);
        header.WriteTo(writer.Stream);

        byte[] record = new byte[recordSize];
        foreach (var (address, methodTable, kind, dependentTarget) in records)
        {
            Array.Clear(record);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), address);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), methodTable);
            record[16] = kind;
            if (version >= 2)
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(20, 8), dependentTarget);

            writer.Stream.Write(record);
        }

        writer.EndSection(records.Length);
        writer.Finish();

        return containerPath;
    }

    private static IHeapAnalysisCache InjectDiskHeapIndex(string containerPath)
    {
        var cache = new HeapAnalysisCache();

        HeapIndexBuildResult buildResult = new(
            StorageKind: HeapIndexStorageKind.Disk,
            IndexPath: containerPath,
            ObjectCount: 0,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>());

        // Same injection point AnalysisPipelineTests.InjectHeapIndex uses: HeapAnalysisCache
        // delegates enumeration to its private HeapIndexCache, so the build result must be set
        // there (not HeapAnalysisCache's own reflection-only backdoor field).
        object heapIndexCache = typeof(HeapAnalysisCache)
            .GetField("_heapIndexCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cache)!;

        typeof(HeapIndexCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(heapIndexCache, buildResult);

        return cache;
    }
}
