using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Tests.Helpers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Format v6's narrowed <c>ObjectSizes</c> column — docs/cache/cache-format-clean-slate-redesign.md
/// §10.1–§10.2. The round-trip cases matter more than the width arithmetic: a sentinel that is read
/// back as a real size is silent corruption, not a failure.
/// </summary>
public class NarrowSizeColumnTests : IDisposable
{
    private readonly string _testDir;

    public NarrowSizeColumnTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "narrow-size-column-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Theory]
    [InlineData(sizeof(ushort))]
    [InlineData(sizeof(uint))]
    [InlineData(sizeof(ulong))]
    public void ReadDiskEntries_RoundTripsEveryWidthIncludingEscapes(int width)
    {
        ulong[] sizes = [24, 64, ushort.MaxValue - 1, ushort.MaxValue, 5_000_000, uint.MaxValue, 8_000_000_000];
        string containerPath = WriteContainer(sizes, width);

        List<HeapEntry> entries = ObjectIndexReader.ReadDiskEntries(containerPath).ToList();

        entries.Select(e => e.Size).Should().Equal(sizes);
    }

    [Fact]
    public void ReadDiskEntriesRange_SeedsTheEscapeCursorFromTheRangeStart()
    {
        // A range enumeration starts mid-column, so the streaming cursor has to be positioned by
        // search rather than assumed to start at the first escape — the per-worker read path in
        // HeapIndexScanDispatcher does exactly this.
        ulong[] sizes = new ulong[64];
        for (int i = 0; i < sizes.Length; i++)
            sizes[i] = i % 4 == 0 ? 1_000_000uL + (ulong)i : 100uL + (ulong)i;

        string containerPath = WriteContainer(sizes, sizeof(ushort));

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        List<HeapEntry> tail = ObjectIndexReader.ReadDiskEntriesRange(reader!, startRecord: 30, recordCount: 20).ToList();

        tail.Select(e => e.Size).Should().Equal(sizes.Skip(30).Take(20));
    }

    [Fact]
    public void ObjectAddressLookup_ResolvesEscapedSizes()
    {
        ulong[] sizes = [24, 900_000, 48];
        string containerPath = WriteContainer(sizes, sizeof(ushort), withSegmentIndex: true);

        ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();
        using (lookup)
        {
            lookup!.TryGetEntry(0x2000, out _, out ulong escapedSize).Should().BeTrue();
            escapedSize.Should().Be(900_000);

            lookup.TryGetEntry(0x1000, out _, out ulong inlineSize).Should().BeTrue();
            inlineSize.Should().Be(24);
        }
    }

    [Fact]
    public void ReadDiskEntries_NarrowedColumnWithoutItsOverflowSection_YieldsNoEntries()
    {
        string containerPath = Path.Combine(_testDir, "cache-no-overflow.bin");
        ulong[] sizes = [24, 900_000, 48];

        using (var writer = new CacheContainerWriter(containerPath))
        {
            ObjectColumnSectionsWriter.WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, Addresses(sizes.Length));
            ObjectColumnSectionsWriter.WriteMethodTableColumns(writer, MethodTables(sizes.Length));

            byte[] narrowed = new byte[sizes.Length * sizeof(ushort)];
            writer.BeginSection(CacheSectionId.ObjectSizes);
            writer.Stream.Write(narrowed, 0, narrowed.Length);
            writer.EndSection(sizes.Length);

            ObjectColumnSectionsWriter.WriteGenerationColumn(writer, new sbyte[sizes.Length]);
            writer.Finish();
        }

        ObjectIndexReader.ReadDiskEntries(containerPath).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1000, 0, 0, sizeof(ushort))]
    [InlineData(1000, 1, 0, sizeof(ushort))]
    [InlineData(1000, 500, 0, sizeof(uint))]
    [InlineData(1000, 500, 500, sizeof(ulong))]
    public void Choose_PicksTheCheapestWidthTheEscapeBudgetAllows(
        long recordCount, long escapesAtTwoBytes, long escapesAtFourBytes, int expectedWidth)
    {
        NarrowColumnWidth.Choose(recordCount, escapesAtTwoBytes, escapesAtFourBytes).Should().Be(expectedWidth);
    }

    [Fact]
    public void Choose_EmptyColumn_StaysFullWidth()
    {
        NarrowColumnWidth.Choose(recordCount: 0, escapesAtTwoBytes: 0, escapesAtFourBytes: 0)
            .Should().Be(NarrowColumnWidth.Full);
    }

    private string WriteContainer(ulong[] sizes, int width, bool withSegmentIndex = false)
    {
        string containerPath = Path.Combine(_testDir, $"cache-{width}-{Guid.NewGuid():N}.bin");
        ulong[] addresses = Addresses(sizes.Length);

        using var writer = new CacheContainerWriter(containerPath);
        ObjectColumnSectionsWriter.WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, addresses);
        ObjectColumnSectionsWriter.WriteMethodTableColumns(writer, MethodTables(sizes.Length));

        if (width == NarrowColumnWidth.Full)
            ObjectColumnSectionsWriter.WriteUlongColumn(writer, CacheSectionId.ObjectSizes, sizes);
        else
            ObjectColumnSectionsWriter.WriteNarrowedSizeColumns(writer, sizes, width);

        ObjectColumnSectionsWriter.WriteGenerationColumn(writer, new sbyte[sizes.Length]);

        if (withSegmentIndex)
        {
            List<SegmentIndexEntry> segments =
                [new(addresses[0], addresses[^1] + 0x1000, firstRecordIndex: 0, recordCount: sizes.Length)];
            writer.BeginSection(CacheSectionId.SegmentIndex);
            SegmentIndexWriter.Write(writer.Stream, segments);
            writer.EndSection(recordCount: segments.Count);
        }

        writer.Finish();
        return containerPath;
    }

    private static ulong[] Addresses(int count) =>
        Enumerable.Range(0, count).Select(i => 0x1000uL + ((ulong)i * 0x1000)).ToArray();

    private static ulong[] MethodTables(int count) =>
        Enumerable.Range(0, count).Select(_ => 0x2000uL).ToArray();
}
