using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

public class CacheContainerReaderAccessorTests : IDisposable
{
    private readonly string _testDir;

    public CacheContainerReaderAccessorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cache-accessor-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void TryOpenSectionAccessor_ValidSection_ReturnsAccessorWithCorrectLength()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        ulong[] values = { 0x1000, 0x1100, 0x1200 };
        WriteUlongColumn(containerPath, values);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? accessor, out long length)
            .Should().BeTrue();

        length.Should().Be(values.Length * sizeof(ulong));
        accessor.Should().NotBeNull();

        for (int i = 0; i < values.Length; i++)
            accessor!.ReadUInt64(i * sizeof(ulong)).Should().Be(values[i]);

        accessor!.Dispose();
    }

    [Fact]
    public void TryOpenSectionAccessor_CorruptedBytes_ReturnsFalse()
    {
        string containerPath = Path.Combine(_testDir, "cache-corrupt.bin");
        WriteUlongColumn(containerPath, new ulong[] { 0x1000, 0x1100, 0x1200 });

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry entry).Should().BeTrue();

        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? corruptedReader).Should().BeTrue();
        corruptedReader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? accessor, out long length)
            .Should().BeFalse();
        accessor.Should().BeNull();
        length.Should().Be(0);
    }

    [Fact]
    public void TryOpenSectionAccessor_ZeroLengthSection_ReturnsTrueWithNullAccessor()
    {
        string containerPath = Path.Combine(_testDir, "cache-empty.bin");

        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.ObjectAddresses);
            writer.EndSection(recordCount: 0);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? accessor, out long length)
            .Should().BeTrue();

        accessor.Should().BeNull();
        length.Should().Be(0);
    }

    private static void WriteUlongColumn(string containerPath, ulong[] values)
    {
        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.ObjectAddresses);
        byte[] buf = new byte[8];
        foreach (ulong value in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            writer.Stream.Write(buf, 0, buf.Length);
        }
        writer.EndSection(recordCount: values.Length);
        writer.Finish();
    }
}
