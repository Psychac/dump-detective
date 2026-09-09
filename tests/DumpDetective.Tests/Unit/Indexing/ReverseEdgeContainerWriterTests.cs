using DumpDetective.Platform.Storage.Container;
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
        reader!.ContainsSection(CacheSectionId.ReverseEdgeDegrees).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeDegreeCheckpoints).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeDegreeOverflow).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeChildren).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeMetadata).Should().BeFalse("format v8 needs no separate metadata section");
        reader.ContainsSection(CacheSectionId.ReverseEdgeOffsets).Should().BeFalse("format v9 replaced the offset column with degrees + checkpoints");
    }

    [Fact]
    public void Write_RoundTripsEveryOffsetThroughTheDegreeColumn()
    {
        // The offsets are no longer stored, so the round-trip that matters is that the degree
        // column plus its checkpoints reconstruct every offset the CSR was built with — including
        // the exclusive end of the last row, which the retired Offsets[RowCount] slot held outright.
        ReverseEdgeCsrResult csr = SampleCsr();
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, csr);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();

        int rowCount = csr.Offsets.Length - 1;
        ReverseEdgeDegreeColumn.TryOpen(reader!, rowCount, out var directory).Should().BeTrue();
        using (directory)
        {
            for (int row = 0; row <= rowCount; row++)
                directory!.GetOffset(row).Should().Be(csr.Offsets[row], $"offset for row {row}");

            for (int row = 0; row < rowCount; row++)
                directory!.GetDegree(row).Should().Be(csr.Offsets[row + 1] - csr.Offsets[row], $"degree for row {row}");
        }

        reader!.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeChildren, out var childrenAccessor, out long childrenLength).Should().BeTrue();
        using (childrenAccessor)
        {
            (childrenLength / sizeof(int)).Should().Be(csr.Children.Length);
            for (int i = 0; i < csr.Children.Length; i++)
                childrenAccessor!.ReadInt32(i * (long)sizeof(int)).Should().Be(csr.Children[i]);
        }
    }

    [Fact]
    public void Write_HubRowExceedingAByte_EscapesAndStillReconstructsOffsets()
    {
        // A degree of 255 or more cannot be stored inline. It must round-trip through the overflow
        // table, and — the part that would silently corrupt every later row if it were wrong — the
        // offsets after it must still come out right, since offsets are summed from degrees.
        const int hubDegree = 300;
        var offsets = new int[4];
        offsets[0] = 0;
        offsets[1] = 1;                 // row 0: one parent
        offsets[2] = 1 + hubDegree;     // row 1: a hub, escapes
        offsets[3] = 1 + hubDegree + 2; // row 2: two parents
        var children = new int[offsets[3]];

        var csr = new ReverseEdgeCsrResult(offsets, children, totalEdges: children.Length);
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, csr);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        ReverseEdgeDegreeColumn.TryOpen(reader!, 3, out var directory).Should().BeTrue();
        using (directory)
        {
            directory!.GetDegree(1).Should().Be(hubDegree);
            for (int row = 0; row <= 3; row++)
                directory.GetOffset(row).Should().Be(offsets[row], $"offset for row {row} must survive the escaped hub before it");
        }
    }

    [Fact]
    public void Write_RowCountSpanningManyCheckpointBlocks_ReconstructsEveryOffset()
    {
        // Exercises the checkpoint arithmetic across block boundaries rather than inside one block:
        // a three-row sample never leaves block 0, so it cannot catch a stride bug.
        int rowCount = ReverseEdgeDegreeColumn.CheckpointStride * 5 + 7;
        var offsets = new int[rowCount + 1];
        for (int row = 0; row < rowCount; row++)
            offsets[row + 1] = offsets[row] + (row % 7);

        var csr = new ReverseEdgeCsrResult(offsets, new int[offsets[rowCount]], totalEdges: offsets[rowCount]);
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, csr);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        ReverseEdgeDegreeColumn.TryOpen(reader!, rowCount, out var directory).Should().BeTrue();
        using (directory)
        {
            for (int row = 0; row <= rowCount; row++)
                directory!.GetOffset(row).Should().Be(offsets[row], $"offset for row {row}");
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
