using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ObjectIndexReaderTests : IDisposable
{
    private readonly string _testDir;

    public ObjectIndexReaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "object-index-reader-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static readonly (ulong Address, ulong MethodTable, ulong Size, sbyte Generation)[] SampleRecords =
    {
        (0x1000uL, 0x2000uL, 100uL, 0),
        (0x1100uL, 0x2100uL, 200uL, 1),
        (0x1200uL, 0x2200uL, 300uL, 2),
    };

    [Fact]
    public void ReadDiskEntries_ValidColumnarContainer_YieldsRecordsInOrder()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        WriteColumnarObjectSections(containerPath, SampleRecords);

        List<HeapEntry> entries = ObjectIndexReader.ReadDiskEntries(containerPath).ToList();

        entries.Should().HaveCount(SampleRecords.Length);
        for (int i = 0; i < SampleRecords.Length; i++)
        {
            entries[i].Address.Should().Be(SampleRecords[i].Address);
            entries[i].MethodTable.Should().Be(SampleRecords[i].MethodTable);
            entries[i].Size.Should().Be(SampleRecords[i].Size);
            entries[i].Generation.Should().Be(SampleRecords[i].Generation);
        }
    }

    [Fact]
    public void ReadDiskEntries_MissingContainer_YieldsNoEntries()
    {
        string containerPath = Path.Combine(_testDir, "does-not-exist.bin");

        ObjectIndexReader.ReadDiskEntries(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void ReadDiskEntries_CorruptedAddressSection_YieldsNoEntries()
    {
        string containerPath = Path.Combine(_testDir, "cache-corrupt.bin");
        WriteColumnarObjectSections(containerPath, SampleRecords);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry entry).Should().BeTrue();

        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        ObjectIndexReader.ReadDiskEntries(containerPath).Should().BeEmpty();
    }

    [Fact]
    public void ReadDiskEntries_MismatchedColumnRecordCounts_YieldsNoEntries()
    {
        string containerPath = Path.Combine(_testDir, "cache-mismatch.bin");

        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, new ulong[] { 0x1000, 0x1100, 0x1200 });
            WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, new ulong[] { 0x2000, 0x2100 });
            WriteUlongColumn(writer, CacheSectionId.ObjectSizes, new ulong[] { 100, 200, 300 });
            writer.Finish();
        }

        ObjectIndexReader.ReadDiskEntries(containerPath).Should().BeEmpty();
    }

    private static void WriteColumnarObjectSections(string containerPath, (ulong Address, ulong MethodTable, ulong Size, sbyte Generation)[] records)
    {
        using var writer = new CacheContainerWriter(containerPath);
        WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, records.Select(r => r.Address).ToArray());
        WriteUlongColumn(writer, CacheSectionId.ObjectMethodTables, records.Select(r => r.MethodTable).ToArray());
        WriteUlongColumn(writer, CacheSectionId.ObjectSizes, records.Select(r => r.Size).ToArray());
        WriteSbyteColumn(writer, CacheSectionId.ObjectGenerations, records.Select(r => r.Generation).ToArray());
        writer.Finish();
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

    private static void WriteSbyteColumn(CacheContainerWriter writer, CacheSectionId id, sbyte[] values)
    {
        writer.BeginSection(id);
        foreach (sbyte value in values)
            writer.Stream.WriteByte(unchecked((byte)value));
        writer.EndSection(recordCount: values.Length);
    }
}
