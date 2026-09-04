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

    /// <summary>
    /// Pins the session semantics from docs/cache/cache-implementation-clean-slate-redesign.md
    /// § 6.1: a section is checksum-verified once per reader instance, not once per open. Corrupting
    /// the bytes after a successful first open and observing that a second open still succeeds is
    /// the only externally-visible proof that the second open didn't re-hash — and it is exactly the
    /// contract narrowing § 6.4(e) records as deliberate (corruption appearing mid-run is no longer
    /// caught, on a file that is renamed into place and never rewritten).
    /// </summary>
    [Fact]
    public void TryOpenSectionAccessor_SameReaderTwice_VerifiesOnlyOnce()
    {
        string containerPath = Path.Combine(_testDir, "cache-verify-once.bin");
        WriteUlongColumn(containerPath, new ulong[] { 0x1000, 0x1100, 0x1200 });

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? first, out _)
            .Should().BeTrue();
        first!.Dispose();

        reader.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry entry).Should().BeTrue();
        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        reader.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? second, out _)
            .Should().BeTrue("the section was already verified by this session, so it is not re-hashed");
        second!.Dispose();

        // A *different* session has no memoized result and must still reject the corruption —
        // the narrowing is per-session, not global.
        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? freshReader).Should().BeTrue();
        freshReader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? third, out _)
            .Should().BeFalse();
        third.Should().BeNull();
    }

    /// <summary>
    /// A section that fails verification stays failed for the session rather than being re-hashed
    /// on every attempt — the negative half of <see cref="TryOpenSectionAccessor_SameReaderTwice_VerifiesOnlyOnce"/>.
    /// </summary>
    [Fact]
    public void TryOpenSectionAccessor_CorruptSectionOpenedTwice_StaysFailedForThatSession()
    {
        string containerPath = Path.Combine(_testDir, "cache-corrupt-twice.bin");
        WriteUlongColumn(containerPath, new ulong[] { 0x1000, 0x1100, 0x1200 });

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? probe).Should().BeTrue();
        probe!.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry entry).Should().BeTrue();
        using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = entry.Offset;
            int b = fs.ReadByte();
            fs.Position = entry.Offset;
            fs.WriteByte((byte)~b);
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out _, out _).Should().BeFalse();
        reader.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// The dispatcher opens one range enumeration per worker over the same sections concurrently
    /// (§ 1a). Verification must be gated per section so those first-touches collapse to one hash
    /// without deadlocking or returning inconsistent results.
    /// </summary>
    [Fact]
    public void TryOpenSectionAccessor_ConcurrentFirstTouches_AllSucceed()
    {
        string containerPath = Path.Combine(_testDir, "cache-concurrent.bin");
        WriteUlongColumn(containerPath, new ulong[] { 0x1000, 0x1100, 0x1200 });

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();

        bool[] results = new bool[16];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = reader!.TryOpenSectionAccessor(
                CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? acc, out _);
            acc?.Dispose();
        });

        results.Should().AllBeEquivalentTo(true);
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
