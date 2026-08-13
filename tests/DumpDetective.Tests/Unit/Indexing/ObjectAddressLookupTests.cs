using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ObjectAddressLookupTests : IDisposable
{
    private readonly string _testDir;

    public ObjectAddressLookupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "object-address-lookup-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // Two segments, deliberately written to the container out of address order (segment 2 has a
    // lower address range than segment 1) — exercises the defensive sort in TryOpen, matching the
    // "segments aren't guaranteed address-sorted" premise in docs/cache/19-ObjectAddressLookupIndex.md.
    // Segment A: [0x1000, 0x1100), 4 objects at 0x1000/0x1040/0x1080/0x10C0.
    // Segment B: [0x5000, 0x5040), 2 objects at 0x5000/0x5020.
    private static readonly (ulong Address, ulong MethodTable, ulong Size)[] SegAObjects =
    {
        (0x1000uL, 0xAAuL, 10uL),
        (0x1040uL, 0xBBuL, 20uL),
        (0x1080uL, 0xAAuL, 30uL),
        (0x10C0uL, 0xCCuL, 40uL),
    };

    private static readonly (ulong Address, ulong MethodTable, ulong Size)[] SegBObjects =
    {
        (0x5000uL, 0xDDuL, 50uL),
        (0x5020uL, 0xEEuL, 60uL),
    };

    [Fact]
    public void TryGetEntry_ExactAddressMatch_ReturnsMethodTableAndSize()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x1080, out ulong mt, out ulong size).Should().BeTrue();
            mt.Should().Be(0xAA);
            size.Should().Be(30);

            lookup.TryGetEntry(0x5020, out ulong mt2, out ulong size2).Should().BeTrue();
            mt2.Should().Be(0xEE);
            size2.Should().Be(60);
        }
    }

    [Fact]
    public void TryGetEntry_SegmentStartAndEndBoundaries_MatchCorrectly()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            // First record of segment A.
            lookup!.TryGetEntry(0x1000, out ulong mt, out ulong size).Should().BeTrue();
            mt.Should().Be(0xAA);
            size.Should().Be(10);

            // Last record of segment A.
            lookup.TryGetEntry(0x10C0, out ulong mtLast, out ulong sizeLast).Should().BeTrue();
            mtLast.Should().Be(0xCC);
            sizeLast.Should().Be(40);
        }
    }

    [Fact]
    public void TryGetEntry_AddressBetweenSegments_ReturnsFalse()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            // 0x2000 is past segment A's end (0x1100) and before segment B's start (0x5000).
            lookup!.TryGetEntry(0x2000, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryGetEntry_AddressBeforeFirstOrAfterLastSegment_ReturnsFalse()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x0, out _, out _).Should().BeFalse();
            lookup.TryGetEntry(0xFFFFFFFF, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryGetEntry_AddressInsideSegmentButNotAnObjectBoundary_ReturnsFalse()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            // 0x1010 falls within segment A's range but doesn't land on an object's address
            // (interior pointer) — out of scope per the design doc, must return false not throw.
            lookup!.TryGetEntry(0x1010, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryGetEntry_AddressInZeroRecordSegmentRange_ReturnsFalse()
    {
        // A segment with zero objects is never written to the SegmentIndex table at all (see
        // SegmentIndexWriter/DiskBackedObjectIndexWriter — "Skip segments with segCount == 0").
        // From the reader's perspective this is indistinguishable from any other address-range gap
        // between recorded segments: no table entry covers it, so the lookup misses. Segment C's
        // range [0x9000, 0x9100) here represents exactly that — a real segment ClrSegment would
        // report, that this container's SegmentIndex simply never recorded.
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x9050, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryOpen_NoSegmentIndexSection_ReturnsFalse()
    {
        string containerPath = Path.Combine(_testDir, "cache-no-segindex.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, new ulong[] { 0x1000 });
            WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, new ulong[] { 0xAA });
            WriteUlongColumn(writer, CacheSectionId.ObjectSizes, new ulong[] { 10 });
            writer.Finish();
        }

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeFalse();
        lookup.Should().BeNull();
    }

    [Fact]
    public void TryOpen_MissingContainer_ReturnsFalse()
    {
        string containerPath = Path.Combine(_testDir, "does-not-exist.bin");

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeFalse();
        lookup.Should().BeNull();
    }

    [Fact]
    public void TryGetEntry_AfterDispose_Throws()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        lookup!.Dispose();

        Action act = () => lookup.TryGetEntry(0x1000, out _, out _);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ObjectIndexReader_TryGetEntry_OneShotWrapper_MatchesObjectAddressLookup()
    {
        string containerPath = WriteTwoSegmentContainer();

        ObjectIndexReader.Instance.TryGetEntry(containerPath, 0x1080, out ulong mt, out ulong size).Should().BeTrue();
        mt.Should().Be(0xAA);
        size.Should().Be(30);

        ObjectIndexReader.Instance.TryGetEntry(containerPath, 0x2000, out _, out _).Should().BeFalse();
    }

    private string WriteTwoSegmentContainer()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");

        (ulong Address, ulong MethodTable, ulong Size)[] all = SegAObjects.Concat(SegBObjects).ToArray();

        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, all.Select(o => o.Address).ToArray());
            WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, all.Select(o => o.MethodTable).ToArray());
            WriteUlongColumn(writer, CacheSectionId.ObjectSizes, all.Select(o => o.Size).ToArray());

            // Segment table deliberately written in reverse-of-address order (B before A) to
            // exercise TryOpen's defensive sort — record ranges still refer to the concatenated
            // column order above (A's 4 records first, then B's 2).
            var segments = new List<SegmentIndexEntry>
            {
                new(0x5000, 0x5040, firstRecordIndex: SegAObjects.Length, recordCount: SegBObjects.Length),
                new(0x1000, 0x1100, firstRecordIndex: 0, recordCount: SegAObjects.Length),
            };
            writer.BeginSection(CacheSectionId.SegmentIndex);
            SegmentIndexWriter.Write(writer.Stream, segments);
            writer.EndSection(recordCount: segments.Count);

            writer.Finish();
        }

        return containerPath;
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
