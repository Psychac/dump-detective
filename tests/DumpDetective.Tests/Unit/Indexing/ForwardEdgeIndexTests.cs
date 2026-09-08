using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ForwardIndex;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Round-trip tests for the forward-edge index (design doc §D5) — mirrors
/// <see cref="ReverseEdgeContainerWriterTests"/>'s shape, plus a
/// <see cref="ForwardEdgeIndexReader.TryGetChildren"/> read-path test the reverse-index suite
/// doesn't need (that index only exposes a "count" enumeration in its own tests elsewhere).
/// </summary>
public class ForwardEdgeIndexTests : IAsyncLifetime
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

    private async Task<(int BucketCount, ForwardEdgeExtractionStats Stats)> BuildSortedBuckets(
        int bucketCount, (ulong parent, ulong child)[] edges)
    {
        var extractor = new ForwardEdgeExtractor(bucketCount, _tempDir);
        foreach (var (parent, child) in edges)
            extractor.RecordEdge(parent, child);
        var stats = extractor.GetStatistics();
        await extractor.DisposeAsync();

        var sorter = new ForwardEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount, CancellationToken.None);

        return (bucketCount, stats);
    }

    private string WriteContainer(int bucketCount, ForwardEdgeExtractionStats stats)
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);
        ForwardEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats);
        writer.Finish();
        return containerPath;
    }

    [Fact]
    public async Task Write_AddsAllThreeSectionsToContainer()
    {
        var (bucketCount, stats) = await BuildSortedBuckets(3,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL), (0x3000UL, 0x0100UL)]);

        string containerPath = WriteContainer(bucketCount, stats);

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.ForwardEdgeBuckets).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ForwardEdgeDirectories).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ForwardEdgeMetadata).Should().BeTrue();
    }

    [Fact]
    public async Task Write_DeletesScratchFilesAfterMerging()
    {
        var (bucketCount, stats) = await BuildSortedBuckets(2,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL)]);

        WriteContainer(bucketCount, stats);

        for (int i = 0; i < bucketCount; i++)
        {
            File.Exists(Path.Combine(_tempDir, $"forward_edges_bucket_{i}.tmp")).Should().BeFalse();
            File.Exists(Path.Combine(_tempDir, $"forward_edges_bucket_{i}.dat")).Should().BeFalse();
            File.Exists(Path.Combine(_tempDir, $"forward_edges_bucket_{i}.idx")).Should().BeFalse();
        }
    }

    [Fact]
    public void Write_DoesNotBumpFormatVersion()
    {
        // §D5 / CacheContainerFormat.cs: additive, following the SegmentIndex precedent — no
        // FormatVersion bump for a purely-optional section.
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
            writer.Finish();

        byte[] headerBytes = File.ReadAllBytes(containerPath)[..CacheFileHeader.Size];
        int version = BitConverter.ToInt32(headerBytes, 8);
        version.Should().Be(CacheFileHeader.CurrentFormatVersion);
    }

    [Fact]
    public async Task Reader_TryGetChildren_RoundTripsExactChildSetPerParent()
    {
        // Two parents sharing no children, one parent with multiple children, one leaf (no
        // children recorded at all) — exercises the group-boundary logic in the sorter/reader.
        (ulong parent, ulong child)[] edges =
        [
            (0x1000UL, 0x0100UL),
            (0x1000UL, 0x0200UL),
            (0x1000UL, 0x0300UL),
            (0x2000UL, 0x0400UL),
        ];

        var (bucketCount, stats) = await BuildSortedBuckets(4, edges);
        string containerPath = WriteContainer(bucketCount, stats);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        ForwardEdgeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetChildren(0x1000UL, out var childrenOf1000).Should().BeTrue();
            childrenOf1000.Should().BeEquivalentTo([0x0100UL, 0x0200UL, 0x0300UL]);

            indexReader.TryGetChildren(0x2000UL, out var childrenOf2000).Should().BeTrue();
            childrenOf2000.Should().BeEquivalentTo([0x0400UL]);

            // Leaf object — never recorded as a parent — must return false, not an empty-but-true result.
            indexReader.TryGetChildren(0x0100UL, out var childrenOfLeaf).Should().BeFalse();
            childrenOfLeaf.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Reader_HandlesHighFanoutParentWithoutTruncation()
    {
        // §D3: out-degree has no fanout cap, unlike the reverse index's 10K-parents-per-child
        // cap — verify a parent with more children than that cap round-trips completely.
        const int childCount = 12_000;
        var edges = new (ulong parent, ulong child)[childCount];
        for (int i = 0; i < childCount; i++)
            edges[i] = (0xAAAAUL, (ulong)(0x1000 + i));

        var (bucketCount, stats) = await BuildSortedBuckets(2, edges);
        string containerPath = WriteContainer(bucketCount, stats);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        ForwardEdgeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetChildren(0xAAAAUL, out var children).Should().BeTrue();
            children.Should().HaveCount(childCount);
        }
    }
}
