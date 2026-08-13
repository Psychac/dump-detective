using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Satellite;

public class SegmentIndexWriterTests : IDisposable
{
    private readonly string _testDir;

    public SegmentIndexWriterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "segment-index-writer-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static readonly SegmentIndexEntry[] SampleEntries =
    {
        new(0x1000, 0x2000, firstRecordIndex: 0, recordCount: 10),
        new(0x5000, 0x6000, firstRecordIndex: 10, recordCount: 25),
        new(0x10000, 0x10100, firstRecordIndex: 35, recordCount: 1),
    };

    [Fact]
    public void WriteThenReadRecords_RoundTrips_AllFields()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        WriteContainer(containerPath, SampleEntries);

        List<SegmentIndexEntry> read = SegmentIndexWriter.ReadRecords(containerPath);

        read.Should().HaveCount(SampleEntries.Length);
        for (int i = 0; i < SampleEntries.Length; i++)
        {
            read[i].Start.Should().Be(SampleEntries[i].Start);
            read[i].End.Should().Be(SampleEntries[i].End);
            read[i].FirstRecordIndex.Should().Be(SampleEntries[i].FirstRecordIndex);
            read[i].RecordCount.Should().Be(SampleEntries[i].RecordCount);
        }
    }

    [Fact]
    public void ReadRecords_NoSegmentIndexSection_ReturnsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "cache-no-section.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.Finish();
        }

        SegmentIndexWriter.ReadRecords(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecords_MissingContainer_ReturnsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "does-not-exist.bin");

        SegmentIndexWriter.ReadRecords(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecords_EmptyEntryList_ReturnsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "cache-empty.bin");
        WriteContainer(containerPath, Array.Empty<SegmentIndexEntry>());

        SegmentIndexWriter.ReadRecords(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecords_CorruptedSectionBytes_ReturnsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "cache-corrupt.bin");
        WriteContainer(containerPath, SampleEntries);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryGetSectionInfo(CacheSectionId.SegmentIndex, out CacheTocEntry entry).Should().BeTrue();

        // Flip a byte inside the section — the container's checksum check (verified before any
        // caller sees section data, per CacheContainerReader.TryOpenSectionAccessor) must reject
        // this the same way ObjectIndexReaderTests' equivalent corruption test does, and
        // ReadRecords must degrade to "unavailable" rather than throw or return partial data.
        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        SegmentIndexWriter.ReadRecords(containerPath).Should().BeEmpty();
    }

    private static void WriteContainer(string containerPath, IReadOnlyList<SegmentIndexEntry> entries)
    {
        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.SegmentIndex);
        SegmentIndexWriter.Write(writer.Stream, entries);
        writer.EndSection(recordCount: entries.Count);
        writer.Finish();
    }
}
