using System.Buffers.Binary;
using System.Reflection;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Cache;

/// <summary>
/// Unit coverage for <see cref="HeapIndexCache.TryGetObjectMetadata"/> — see
/// docs/cache/19-ObjectAddressLookupIndex.md Phase 3. Only the disk-hit path is exercised here
/// (no <c>ClrHeap</c> dependency); the live-fallback path (in-memory mode / unavailable
/// SegmentIndex) needs a real <c>ClrHeap</c> and is covered by the discrepancy test suite instead.
/// </summary>
public class HeapIndexCacheTests : IDisposable
{
    private readonly string _testDir;

    public HeapIndexCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "heap-index-cache-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static readonly (ulong Address, ulong MethodTable, ulong Size)[] Objects =
    {
        (0x1000uL, 0xAAuL, 10uL),
        (0x1040uL, 0xBBuL, 20uL),
    };

    [Fact]
    public void TryGetObjectMetadata_DiskIndexHit_ResolvesWithoutTouchingHeap()
    {
        HeapIndexCache cache = new();
        SeedDiskHeapIndex(cache, WriteContainer());

        // heap is passed as null! — if the disk-hit path touched it, this would NullReferenceException.
        bool found = cache.TryGetObjectMetadata(null!, 0x1040, out ulong mt, out ulong size);

        found.Should().BeTrue();
        mt.Should().Be(0xBB);
        size.Should().Be(20);
    }

    [Fact]
    public void TryGetObjectMetadata_DiskIndexMiss_ReturnsFalseWithoutTouchingHeap()
    {
        HeapIndexCache cache = new();
        SeedDiskHeapIndex(cache, WriteContainer());

        bool found = cache.TryGetObjectMetadata(null!, 0x9999, out ulong mt, out ulong size);

        found.Should().BeFalse();
        mt.Should().Be(0);
        size.Should().Be(0);
    }

    [Fact]
    public void TryGetObjectMetadata_ZeroAddress_NoHeapIndex_ReturnsFalseWithoutTouchingHeap()
    {
        HeapIndexCache cache = new();

        bool found = cache.TryGetObjectMetadata(null!, 0, out ulong mt, out ulong size);

        found.Should().BeFalse();
        mt.Should().Be(0);
        size.Should().Be(0);
    }

    [Fact]
    public void TryGetObjectMetadata_DiskIndexWithoutSegmentIndexSection_FallsThroughPastDiskPath()
    {
        // Container has the object columns but no SegmentIndex section (old cache /
        // DD_SKIP_SEGMENT_INDEX_BUILD=1) — the disk path must report "unavailable" (not throw),
        // leaving the zero-address short-circuit as the only heap-free assertion available here.
        string containerPath = Path.Combine(_testDir, "cache-no-segindex.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, Objects.Select(o => o.Address).ToArray());
            WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, Objects.Select(o => o.MethodTable).ToArray());
            WriteUlongColumn(writer, CacheSectionId.ObjectSizes, Objects.Select(o => o.Size).ToArray());
            writer.Finish();
        }

        HeapIndexCache cache = new();
        SeedDiskHeapIndex(cache, containerPath);

        bool found = cache.TryGetObjectMetadata(null!, 0, out _, out _);
        found.Should().BeFalse();
    }

    [Fact]
    public void Dispose_ReleasesAddressLookup_WithoutThrowing()
    {
        HeapIndexCache cache = new();
        SeedDiskHeapIndex(cache, WriteContainer());
        cache.TryGetObjectMetadata(null!, 0x1000, out _, out _);

        Action act = () => cache.Dispose();

        act.Should().NotThrow();
    }

    private string WriteContainer()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);
        WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, Objects.Select(o => o.Address).ToArray());
        WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, Objects.Select(o => o.MethodTable).ToArray());
        WriteUlongColumn(writer, CacheSectionId.ObjectSizes, Objects.Select(o => o.Size).ToArray());

        var segments = new List<SegmentIndexEntry> { new(0x1000, 0x1100, firstRecordIndex: 0, recordCount: Objects.Length) };
        writer.BeginSection(CacheSectionId.SegmentIndex);
        SegmentIndexWriter.Write(writer.Stream, segments);
        writer.EndSection(recordCount: segments.Count);

        writer.Finish();
        return containerPath;
    }

    private static void SeedDiskHeapIndex(HeapIndexCache cache, string containerPath)
    {
        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            containerPath,
            ObjectCount: Objects.Length,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>());

        typeof(HeapIndexCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cache, heapIndex);
    }

    private static void WriteUlongColumn(CacheContainerWriter writer, CacheSectionId id, ulong[] values)
    {
        writer.BeginSection(id);
        byte[] buf = new byte[8];
        foreach (ulong value in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            writer.Stream.Write(buf, 0, buf.Length);
        }
        writer.EndSection(recordCount: values.Length);
    }
}
