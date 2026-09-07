using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// §10.1/§10.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — mirrors
/// <c>ObjectAddressLookupTests</c>'s scenarios, but against raw per-segment scratch files instead of
/// a finalized container, since that's the whole point of this class: usable mid-Phase-1-build,
/// before a container with a complete TOC exists.
/// </summary>
public class ScratchFileObjectMetadataLookupTests : IDisposable
{
    private readonly string _testDir;

    public ScratchFileObjectMetadataLookupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "scratch-metadata-lookup-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // Segment A: [0x1000, 0x1100), 4 objects at 0x1000/0x1040/0x1080/0x10C0.
    private static readonly (ulong Address, ulong MethodTable, ulong Size)[] SegAObjects =
    {
        (0x1000uL, 0xAAuL, 10uL),
        (0x1040uL, 0xBBuL, 20uL),
        (0x1080uL, 0xAAuL, 30uL),
        (0x10C0uL, 0xCCuL, 40uL),
    };

    // Segment B: [0x5000, 0x5040), 2 objects at 0x5000/0x5020.
    private static readonly (ulong Address, ulong MethodTable, ulong Size)[] SegBObjects =
    {
        (0x5000uL, 0xDDuL, 50uL),
        (0x5020uL, 0xEEuL, 60uL),
    };

    private static void WriteColumn(string path, ulong[] values)
    {
        Span<byte> buf = stackalloc byte[8];
        using FileStream stream = File.Create(path);
        foreach (ulong value in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            stream.Write(buf);
        }
    }

    private ScratchSegmentSource WriteSegment(
        string name, ulong start, ulong end, (ulong Address, ulong MethodTable, ulong Size)[] objects)
    {
        string addrPath = Path.Combine(_testDir, $"{name}.addr");
        string mtPath = Path.Combine(_testDir, $"{name}.mt");
        string sizePath = Path.Combine(_testDir, $"{name}.size");

        WriteColumn(addrPath, objects.Select(o => o.Address).ToArray());
        WriteColumn(mtPath, objects.Select(o => o.MethodTable).ToArray());
        WriteColumn(sizePath, objects.Select(o => o.Size).ToArray());

        var entry = new SegmentIndexEntry(start, end, firstRecordIndex: 0, objects.Length);
        return new ScratchSegmentSource(entry, addrPath, mtPath, sizePath);
    }

    private (ScratchSegmentSource A, ScratchSegmentSource B) WriteTwoSegments() =>
        (WriteSegment("segA", 0x1000, 0x1100, SegAObjects), WriteSegment("segB", 0x5000, 0x5040, SegBObjects));

    [Fact]
    public void TryGetEntry_ExactAddressMatch_ReturnsMethodTableAndSize()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
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
    public void TryGetEntry_SegmentsSuppliedOutOfAddressOrder_StillResolvesCorrectly()
    {
        // Same "segment write order isn't address-sorted" premise ObjectAddressLookup defends
        // against — pass B before A.
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();

        ScratchFileObjectMetadataLookup.TryOpen([segB, segA], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x1000, out ulong mt, out ulong size).Should().BeTrue();
            mt.Should().Be(0xAA);
            size.Should().Be(10);
        }
    }

    [Fact]
    public void TryGetEntry_AddressBetweenSegments_ReturnsFalse()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x3000, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryGetEntry_AddressInsideSegmentButNotAnObjectBoundary_ReturnsFalse()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x1010, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryOpen_OneSegmentMissingScratchFile_OpensOthersAndSkipsIt()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();
        File.Delete(segB.AddressPath);

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x1000, out ulong mt, out _).Should().BeTrue();
            mt.Should().Be(0xAA);

            lookup.TryGetEntry(0x5000, out _, out _).Should().BeFalse("segment B's scratch files couldn't be opened");
        }
    }

    [Fact]
    public void TryOpen_EmptySegmentList_ReturnsFalse()
    {
        ScratchFileObjectMetadataLookup.TryOpen([], out ScratchFileObjectMetadataLookup? lookup).Should().BeFalse();
        lookup.Should().BeNull();
    }

    [Fact]
    public void TryOpen_AllScratchFilesMissing_ReturnsFalse()
    {
        (ScratchSegmentSource segA, _) = WriteTwoSegments();
        File.Delete(segA.AddressPath);
        File.Delete(segA.MethodTablePath);
        File.Delete(segA.SizePath);

        ScratchFileObjectMetadataLookup.TryOpen([segA], out ScratchFileObjectMetadataLookup? lookup).Should().BeFalse();
        lookup.Should().BeNull();
    }

    [Fact]
    public void TryGetEntry_AfterDispose_Throws()
    {
        (ScratchSegmentSource segA, _) = WriteTwoSegments();
        ScratchFileObjectMetadataLookup.TryOpen([segA], out ScratchFileObjectMetadataLookup? lookup);
        lookup!.Dispose();

        Action act = () => lookup.TryGetEntry(0x1000, out _, out _);
        act.Should().Throw<ObjectDisposedException>();
    }

    // Ascending, spans both segments, includes one address between them (0x3000, resolves to
    // nothing) and one inside a segment but not an object boundary (0x1010, also resolves to
    // nothing) — exercises the "leave caller's default" contract on both paths.
    private static readonly ulong[] AscendingBatchAddresses = [0x1000, 0x1010, 0x1080, 0x3000, 0x5020];

    [Fact]
    public void ResolveBatch_DefaultPath_ResolvesOutOfOrderAddresses()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();
        ulong[] addresses = [0x5020, 0x1000, 0x3000, 0x1080, 0x1010];
        var methodTables = new ulong[addresses.Length];
        var sizes = new ulong[addresses.Length];

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.ResolveBatch(addresses, methodTables, sizes, CancellationToken.None);
        }

        methodTables.Should().Equal(0xEEuL, 0xAAuL, 0uL, 0xAAuL, 0uL);
        sizes.Should().Equal(60uL, 10uL, 0uL, 30uL, 0uL);
    }

    [Fact]
    public void ResolveBatch_AscendingFastPath_MatchesDefaultPathOnSameInput()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();
        var defaultMethodTables = new ulong[AscendingBatchAddresses.Length];
        var defaultSizes = new ulong[AscendingBatchAddresses.Length];
        var fastMethodTables = new ulong[AscendingBatchAddresses.Length];
        var fastSizes = new ulong[AscendingBatchAddresses.Length];

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.ResolveBatch(AscendingBatchAddresses, defaultMethodTables, defaultSizes, CancellationToken.None);
            lookup.ResolveBatch(
                AscendingBatchAddresses, fastMethodTables, fastSizes, CancellationToken.None,
                assumeAscendingAddresses: true);
        }

        fastMethodTables.Should().Equal(defaultMethodTables);
        fastSizes.Should().Equal(defaultSizes);
        fastMethodTables.Should().Equal(0xAAuL, 0uL, 0xAAuL, 0uL, 0xEEuL);
        fastSizes.Should().Equal(10uL, 0uL, 30uL, 0uL, 60uL);
    }

    [Fact]
    public void ResolveBatch_AscendingFastPath_ThrowsOnOutOfOrderInput()
    {
        (ScratchSegmentSource segA, ScratchSegmentSource segB) = WriteTwoSegments();
        ulong[] outOfOrder = [0x1080, 0x1000, 0x5020];
        var methodTables = new ulong[outOfOrder.Length];
        var sizes = new ulong[outOfOrder.Length];

        ScratchFileObjectMetadataLookup.TryOpen([segA, segB], out ScratchFileObjectMetadataLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            Action act = () => lookup!.ResolveBatch(
                outOfOrder, methodTables, sizes, CancellationToken.None,
                assumeAscendingAddresses: true);
            act.Should().Throw<InvalidOperationException>().WithMessage("*out-of-order*index 1*");
        }
    }
}
