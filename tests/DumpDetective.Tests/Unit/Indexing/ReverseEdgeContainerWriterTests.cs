using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Phase C of format v8's true CSR reverse-edge index — writes an already-built
/// <see cref="ReverseEdgeCsrResult"/> straight into the container, the same shape
/// <see cref="Dominator.DominatorTreeIndexWriter"/> uses for its own row-aligned columns. No merge,
/// no per-bucket byte ranges, no separate metadata section, since a reader recovers the row count
/// from the TOC.
/// </summary>
public class ReverseEdgeContainerWriterTests : IAsyncLifetime
{
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        await Task.CompletedTask;
    }

    private static ReverseEdgeCsrResult SampleCsr() =>
        // Row 0: no parents. Row 1: parents at rows 0 and 2. Row 2: no parents.
        new(offsets: [0, 0, 2, 2], children: [0, 2], totalEdges: 2);

    [Fact]
    public void Write_AddsBothSectionsToContainer()
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, SampleCsr());
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.ReverseEdgeOffsets).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeChildren).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeMetadata).Should().BeFalse("format v8 needs no separate metadata section");
    }

    [Fact]
    public void Write_RoundTripsOffsetsAndChildrenExactly()
    {
        ReverseEdgeCsrResult csr = SampleCsr();
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, csr);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();

        reader!.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeOffsets, out var offsetsAccessor, out long offsetsLength).Should().BeTrue();
        using (offsetsAccessor)
        {
            (offsetsLength / sizeof(int)).Should().Be(csr.Offsets.Length);
            for (int i = 0; i < csr.Offsets.Length; i++)
                offsetsAccessor!.ReadInt32(i * (long)sizeof(int)).Should().Be(csr.Offsets[i]);
        }

        reader.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeChildren, out var childrenAccessor, out long childrenLength).Should().BeTrue();
        using (childrenAccessor)
        {
            (childrenLength / sizeof(int)).Should().Be(csr.Children.Length);
            for (int i = 0; i < csr.Children.Length; i++)
                childrenAccessor!.ReadInt32(i * (long)sizeof(int)).Should().Be(csr.Children[i]);
        }
    }

    [Fact]
    public void Write_EmptyChildren_StillOpensAsPresentNotMissing()
    {
        // A dump with reachable rows but literally zero recorded edges is a legitimate (if
        // unusual) answer, not a corrupt container -- ReverseEdgeChildren length 0 must still open.
        var csr = new ReverseEdgeCsrResult(offsets: [0, 0], children: [], totalEdges: 0);
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, csr);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeChildren, out var accessor, out long length).Should().BeTrue();
        length.Should().Be(0);
        accessor?.Dispose();
    }

    [Fact]
    public void Write_StampsCurrentFormatVersion()
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, SampleCsr());
            writer.Finish();
        }

        byte[] headerBytes = File.ReadAllBytes(containerPath)[..CacheFileHeader.Size];
        int version = BitConverter.ToInt32(headerBytes, 8);

        // Asserted against the constant rather than a literal: the point is that the writer stamps
        // whatever the current version is, not that the version happens to be any given number.
        version.Should().Be(CacheFileHeader.CurrentFormatVersion);
    }

    [Fact]
    public void TryOpen_RejectsPreExistingV3Container()
    {
        // Simulate a cache.bin written by pre-Phase-C code (FormatVersion 3): a current reader must
        // fail cleanly (treated as a cold cache) rather than misinterpret the missing sections.
        string containerPath = Path.Combine(_tempDir, "v3-cache.bin");
        using (var fs = File.Create(containerPath))
        {
            var buf = new byte[CacheFileHeader.Size];
            "DDCACHE1"u8.CopyTo(buf);
            BitConverter.GetBytes(3).CopyTo(buf, 8); // FormatVersion = 3 (stale)
            BitConverter.GetBytes(0).CopyTo(buf, 44); // SectionCount = 0
            BitConverter.GetBytes((long)CacheFileHeader.Size).CopyTo(buf, 48); // TocOffset
            fs.Write(buf);
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeFalse();
        reader.Should().BeNull();
    }
}
