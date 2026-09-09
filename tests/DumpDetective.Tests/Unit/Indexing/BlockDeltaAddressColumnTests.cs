using DumpDetective.Analysis.Indexing;
using DumpDetective.Platform.Storage.Columns;
using DumpDetective.Platform.Storage.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Tests.Helpers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Format v6's block-delta address columns — docs/cache/cache-format-clean-slate-redesign.md §10.3
/// and §10.4. The cases that matter are the ones the encoding is total over: a delta that doesn't
/// fit, a descending step, and a block boundary landing mid-batch.
/// </summary>
public class BlockDeltaAddressColumnTests : IDisposable
{
    private readonly string _testDir;

    public BlockDeltaAddressColumnTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "block-delta-column-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void ReadDiskEntries_RoundTripsAddressesSpanningManyBlocks()
    {
        ulong[] addresses = Ascending(BlockDeltaColumn.BlockRecords * 3 + 7);
        string containerPath = WriteContainer(addresses);

        ObjectIndexReader.ReadDiskEntries(containerPath).Select(e => e.Address).Should().Equal(addresses);
    }

    [Fact]
    public void ReadDiskEntries_RoundTripsDeltasTooLargeForFourBytes()
    {
        // The 4 GB step is exactly the inter-segment gap that produced the reference dump's 16
        // escaped records; the descending step is the case the encoding tolerates but no measured
        // dump exhibits.
        ulong[] addresses = [0x1000, 0x2000, 0x1_0000_2000, 0x1_0000_3000, 0x500, 0x1_0000_4000];
        string containerPath = WriteContainer(addresses);

        ObjectIndexReader.ReadDiskEntries(containerPath).Select(e => e.Address).Should().Equal(addresses);
    }

    [Fact]
    public void ReadDiskEntriesRange_SeedsTheEscapeCursorFromTheRangeStart()
    {
        ulong[] addresses = new ulong[BlockDeltaColumn.BlockRecords * 2];
        ulong cursor = 0x1000;
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = cursor;
            cursor += i % 500 == 499 ? 0x1_0000_0000uL : 0x40uL;
        }

        string containerPath = WriteContainer(addresses);
        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();

        long start = BlockDeltaColumn.BlockRecords + 3;
        ObjectIndexReader.ReadDiskEntriesRange(reader!, start, 600)
            .Select(e => e.Address)
            .Should().Equal(addresses.Skip((int)start).Take(600));
    }

    [Fact]
    public void ObjectAddressLookup_BinarySearchDecodesEveryProbe()
    {
        ulong[] addresses = Ascending(BlockDeltaColumn.BlockRecords * 2 + 5);
        string containerPath = WriteContainer(addresses, withSegmentIndex: true);

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            foreach (int index in new[] { 0, 1, 1023, 1024, 1025, addresses.Length - 1 })
                lookup!.TryGetEntry(addresses[index], out _, out _).Should().BeTrue($"record {index} must be findable");

            lookup!.TryGetEntry(addresses[0] + 4, out _, out _).Should().BeFalse("an interior pointer is not a record");
        }
    }

    [Fact]
    public void ReadDiskEntries_DeltaColumnWithoutItsBlockBases_YieldsNoEntries()
    {
        string containerPath = Path.Combine(_testDir, "cache-no-bases.bin");
        ulong[] addresses = Ascending(16);

        using (var writer = new CacheContainerWriter(containerPath))
        {
            byte[] deltas = new byte[addresses.Length * BlockDeltaColumn.DeltaWidth];
            writer.BeginSection(CacheSectionId.ObjectAddresses);
            writer.Stream.Write(deltas, 0, deltas.Length);
            writer.EndSection(addresses.Length);

            ObjectColumnSectionsWriter.WriteMethodTableColumns(writer, MethodTables(addresses.Length));
            ObjectColumnSectionsWriter.WriteUlongColumn(writer, CacheSectionId.ObjectSizes, new ulong[addresses.Length]);
            ObjectColumnSectionsWriter.WriteGenerationColumn(writer, new sbyte[addresses.Length]);
            writer.Finish();
        }

        ObjectIndexReader.ReadDiskEntries(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void DominatorReachableAddresses_RoundTripsAcrossBlocksAndEscapes()
    {
        string containerPath = Path.Combine(_testDir, "cache-dominator.bin");
        ulong[] reachable = new ulong[BlockDeltaColumn.BlockRecords + 40];
        ulong cursor = 0x2000;
        for (int i = 0; i < reachable.Length; i++)
        {
            reachable[i] = cursor;
            cursor += i == 700 ? 0x2_0000_0000uL : 0x30uL;
        }

        using (var writer = new CacheContainerWriter(containerPath))
        {
            ObjectColumnSectionsWriter.WriteReachableRows(writer, reachable);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        DominatorReachableAddressReader.TryOpen(reader!, out DominatorReachableAddressReader? reachableReader).Should().BeTrue();

        using (reachableReader)
        {
            foreach (ulong address in reachable)
                reachableReader!.IsReachable(address).Should().BeTrue($"0x{address:X} was written as reachable");

            reachableReader!.IsReachable(reachable[^1] + 8).Should().BeFalse();
        }
    }

    private string WriteContainer(ulong[] addresses, bool withSegmentIndex = false)
    {
        string containerPath = Path.Combine(_testDir, $"cache-{Guid.NewGuid():N}.bin");

        using var writer = new CacheContainerWriter(containerPath);
        ObjectColumnSectionsWriter.WriteBlockDeltaAddressColumns(writer, addresses);
        ObjectColumnSectionsWriter.WriteMethodTableColumns(writer, MethodTables(addresses.Length));
        ObjectColumnSectionsWriter.WriteUlongColumn(writer, CacheSectionId.ObjectSizes, new ulong[addresses.Length]);
        ObjectColumnSectionsWriter.WriteGenerationColumn(writer, new sbyte[addresses.Length]);

        if (withSegmentIndex)
        {
            List<SegmentIndexEntry> segments =
                [new(addresses[0], addresses[^1] + 0x1000, firstRecordIndex: 0, recordCount: addresses.Length)];
            writer.BeginSection(CacheSectionId.SegmentIndex);
            SegmentIndexWriter.Write(writer.Stream, segments);
            writer.EndSection(recordCount: segments.Count);
        }

        writer.Finish();
        return containerPath;
    }

    private static ulong[] Ascending(int count) =>
        Enumerable.Range(0, count).Select(i => 0x1000uL + ((ulong)i * 0x40)).ToArray();

    private static ulong[] MethodTables(int count) =>
        Enumerable.Range(0, count).Select(_ => 0x2000uL).ToArray();
}
