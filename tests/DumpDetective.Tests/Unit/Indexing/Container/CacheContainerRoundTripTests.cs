using System.Buffers.Binary;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

public class CacheContainerRoundTripTests : IDisposable
{
    private readonly string _testDir;

    public CacheContainerRoundTripTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cache-roundtrip-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void WriteAndRead_SectionsMatchExactly()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");

        // Write a container with test data
        WriteTestContainer(containerPath);

        // Read it back
        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader.Should().NotBeNull();

        // Verify all sections exist with correct metadata
        reader!.ContainsSection(CacheSectionId.Objects).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.TypeAggregates).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.Roots).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.Handles).Should().BeTrue();
    }

    [Fact]
    public void WriteAndRead_RecordCountsMatch()
    {
        string containerPath = Path.Combine(_testDir, "cache-counts.bin");

        WriteTestContainer(containerPath);
        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();

        reader!.TryGetSectionInfo(CacheSectionId.Objects, out var objEntry).Should().BeTrue();
        objEntry.RecordCount.Should().Be(3);

        reader.TryGetSectionInfo(CacheSectionId.Roots, out var rootEntry).Should().BeTrue();
        rootEntry.RecordCount.Should().Be(2);

        reader.TryGetSectionInfo(CacheSectionId.Handles, out var handleEntry).Should().BeTrue();
        handleEntry.RecordCount.Should().Be(1);
    }

    [Fact]
    public void ReadNonExistentContainer_ReturnsFalse()
    {
        string missingPath = Path.Combine(_testDir, "does-not-exist.bin");
        CacheContainerReader.TryOpen(missingPath, out var reader).Should().BeFalse();
        reader.Should().BeNull();
    }

    [Fact]
    public void ReadCorruptedHeader_ReturnsFalse()
    {
        string containerPath = Path.Combine(_testDir, "corrupted.bin");

        // Write garbage that doesn't match magic
        File.WriteAllBytes(containerPath, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeFalse();
        reader.Should().BeNull();
    }

    [Fact]
    public void TryOpenSection_CorruptedBytes_ReturnsFalse()
    {
        string containerPath = Path.Combine(_testDir, "cache-corrupt-section.bin");

        WriteTestContainer(containerPath);

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.TryGetSectionInfo(CacheSectionId.Roots, out var entry).Should().BeTrue();

        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        CacheContainerReader.TryOpen(containerPath, out var corruptedReader).Should().BeTrue();
        corruptedReader!.TryOpenSection(CacheSectionId.Roots, out var sectionStream).Should().BeFalse();
        sectionStream.Should().BeNull();

        // Unaffected sections still open successfully.
        corruptedReader.TryOpenSection(CacheSectionId.Handles, out var handleStream).Should().BeTrue();
        handleStream!.Dispose();
    }

    [Fact]
    public void MultipleReads_SectionStreamOpenable()
    {
        string containerPath = Path.Combine(_testDir, "cache-consistent.bin");

        WriteTestContainer(containerPath);

        // Verify we can open the same section multiple times
        CacheContainerReader.TryOpen(containerPath, out var reader1).Should().BeTrue();
        reader1!.TryOpenSection(CacheSectionId.Objects, out var stream1).Should().BeTrue();
        stream1!.Dispose();

        CacheContainerReader.TryOpen(containerPath, out var reader2).Should().BeTrue();
        reader2!.TryOpenSection(CacheSectionId.Objects, out var stream2).Should().BeTrue();
        stream2!.Dispose();
    }

    private void WriteTestContainer(string containerPath)
    {
        using var writer = new CacheContainerWriter(containerPath);

        // Write Objects section: 3 records (address + methodtable + size)
        writer.BeginSection(CacheSectionId.Objects);
        WriteIndexHeader(writer.Stream, recordCount: 3);
        WriteObjectRecords(writer.Stream, new[] {
            (0x1000uL, 0x2000uL, 100uL),
            (0x1100uL, 0x2100uL, 200uL),
            (0x1200uL, 0x2200uL, 300uL)
        });
        writer.EndSection(recordCount: 3);

        // Write TypeAggregates section: 1 entry (placeholder)
        writer.BeginSection(CacheSectionId.TypeAggregates);
        WriteIndexHeader(writer.Stream, recordCount: 1);
        writer.Stream.Write(new byte[100], 0, 100);
        writer.EndSection(recordCount: 1);

        // Write Roots section: 2 records (target + root + kind)
        writer.BeginSection(CacheSectionId.Roots);
        WriteIndexHeader(writer.Stream, recordCount: 2);
        WriteRootRecords(writer.Stream, new[] {
            (target: 0x3000uL, root: 0x4000uL, kind: (byte)1),
            (target: 0x3100uL, root: 0x4100uL, kind: (byte)2)
        });
        writer.EndSection(recordCount: 2);

        // Write Handles section: 1 record
        writer.BeginSection(CacheSectionId.Handles);
        WriteIndexHeader(writer.Stream, recordCount: 1);
        WriteHandleRecords(writer.Stream, new[] {
            (addr: 0x5000uL, mt: 0x6000uL, kind: (byte)1)
        });
        writer.EndSection(recordCount: 1);

        writer.Finish();
    }

    private void WriteIndexHeader(Stream stream, long recordCount)
    {
        const int HeaderSize = 24;
        const int Magic = 0x58495452; // "RTIX"
        const int Version = 1;

        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), recordCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 0); // flags/reserved
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), 0); // reserved

        stream.Write(header, 0, header.Length);
    }

    private void WriteObjectRecords(Stream stream, (ulong addr, ulong mt, ulong size)[] records)
    {
        byte[] record = new byte[24]; // addr(8) + methodtable(8) + size(8)
        foreach (var (addr, mt, size) in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), addr);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), mt);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16, 8), size);
            stream.Write(record, 0, record.Length);
        }
    }

    private void WriteRootRecords(Stream stream, (ulong target, ulong root, byte kind)[] records)
    {
        byte[] record = new byte[20]; // target(8) + root(8) + kind(1) + pad(3)
        foreach (var (target, root, kind) in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), target);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), root);
            record[16] = kind;
            stream.Write(record, 0, record.Length);
        }
    }

    private void WriteHandleRecords(Stream stream, (ulong addr, ulong mt, byte kind)[] records)
    {
        byte[] record = new byte[20]; // addr(8) + methodtable(8) + kind(1) + pad(3)
        foreach (var (addr, mt, kind) in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), addr);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), mt);
            record[16] = kind;
            stream.Write(record, 0, record.Length);
        }
    }


}
