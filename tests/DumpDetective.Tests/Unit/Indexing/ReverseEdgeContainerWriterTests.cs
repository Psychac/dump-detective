using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

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

    private async Task<(int BucketCount, ReverseEdgeExtractionStats Stats, byte[][] DatBytes, byte[][] IdxBytes)>
        BuildSortedBuckets(int bucketCount, (ulong parent, ulong child)[] edges)
    {
        var extractor = new ReverseEdgeExtractor(bucketCount, _tempDir);
        foreach (var (parent, child) in edges)
            extractor.RecordEdge(parent, child);
        var stats = extractor.GetStatistics();
        await extractor.DisposeAsync();

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount, CancellationToken.None);

        var datBytes = new byte[bucketCount][];
        var idxBytes = new byte[bucketCount][];
        for (int i = 0; i < bucketCount; i++)
        {
            string datFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{i}.dat");
            string idxFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{i}.idx");
            datBytes[i] = File.Exists(datFile) ? File.ReadAllBytes(datFile) : Array.Empty<byte>();
            idxBytes[i] = File.Exists(idxFile) ? File.ReadAllBytes(idxFile) : Array.Empty<byte>();
        }

        return (bucketCount, stats, datBytes, idxBytes);
    }

    [Fact]
    public async Task Write_AddsAllThreeSectionsToContainer()
    {
        var (bucketCount, stats, _, _) = await BuildSortedBuckets(3,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL), (0x3000UL, 0x0100UL)]);

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.ReverseEdgeBuckets).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeDirectories).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.ReverseEdgeMetadata).Should().BeTrue();
    }

    [Fact]
    public async Task Write_MetadataDescribesEachBucketByteRangeCorrectly()
    {
        var (bucketCount, stats, expectedDat, expectedIdx) = await BuildSortedBuckets(3,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL), (0x3000UL, 0x0100UL), (0x4000UL, 0x0300UL)]);

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();

        reader!.TryOpenSection(CacheSectionId.ReverseEdgeMetadata, out var metaStream).Should().BeTrue();
        var metadata = JsonSerializer.Deserialize<ReverseIndexMetadata>(metaStream!)!;
        metadata.BucketCount.Should().Be(bucketCount);
        metadata.Buckets.Should().HaveCount(bucketCount);

        reader.TryOpenSection(CacheSectionId.ReverseEdgeBuckets, out var dataSectionStream).Should().BeTrue();
        using var dataMs = new MemoryStream();
        dataSectionStream!.CopyTo(dataMs);
        byte[] dataSectionBytes = dataMs.ToArray();

        reader.TryOpenSection(CacheSectionId.ReverseEdgeDirectories, out var dirSectionStream).Should().BeTrue();
        using var dirMs = new MemoryStream();
        dirSectionStream!.CopyTo(dirMs);
        byte[] dirSectionBytes = dirMs.ToArray();

        foreach (var bucket in metadata.Buckets)
        {
            byte[] actualDat = dataSectionBytes.AsSpan((int)bucket.DataOffset, (int)bucket.DataLength).ToArray();
            byte[] actualIdx = dirSectionBytes.AsSpan((int)bucket.DirectoryOffset, (int)bucket.DirectoryLength).ToArray();

            actualDat.Should().Equal(expectedDat[bucket.BucketIndex]);
            actualIdx.Should().Equal(expectedIdx[bucket.BucketIndex]);
        }
    }

    [Fact]
    public async Task Write_DeletesScratchFilesAfterMerging()
    {
        var (bucketCount, stats, _, _) = await BuildSortedBuckets(2,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL)]);

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats);
            writer.Finish();
        }

        for (int i = 0; i < bucketCount; i++)
        {
            File.Exists(Path.Combine(_tempDir, $"reverse_edges_bucket_{i}.tmp")).Should().BeFalse();
            File.Exists(Path.Combine(_tempDir, $"reverse_edges_bucket_{i}.dat")).Should().BeFalse();
            File.Exists(Path.Combine(_tempDir, $"reverse_edges_bucket_{i}.idx")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Write_BumpsFormatVersionTo4()
    {
        var (bucketCount, stats, _, _) = await BuildSortedBuckets(1, [(0x1000UL, 0x0100UL)]);

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats);
            writer.Finish();
        }

        byte[] headerBytes = File.ReadAllBytes(containerPath)[..CacheFileHeader.Size];
        int version = BitConverter.ToInt32(headerBytes, 8);
        version.Should().Be(4);
    }

    [Fact]
    public void TryOpen_RejectsPreExistingV3Container()
    {
        // Simulate a cache.bin written by pre-Phase-C code (FormatVersion 3): a v4 reader must
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

    [Fact]
    public async Task Write_ReportsProgressForBothDataAndDirectoryPasses()
    {
        var (bucketCount, stats, _, _) = await BuildSortedBuckets(3,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL), (0x3000UL, 0x0100UL)]);

        var reports = new List<AnalyzerProgressReport>();
        var progress = new SynchronousProgress<AnalyzerProgressReport>(r => reports.Add(r));

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ReverseEdgeContainerWriter.Write(writer, _tempDir, bucketCount, stats, progress);
            writer.Finish();
        }

        reports.Should().OnlyContain(r => r.Phase == "merging reverse-index into cache.bin");
        reports.Where(r => r.Detail!.Contains("data")).Should().HaveCount(bucketCount);
        reports.Where(r => r.Detail!.Contains("directory")).Should().HaveCount(bucketCount);
    }

    /// <summary>Invokes the callback synchronously — unlike <see cref="Progress{T}"/>, which posts
    /// to the captured SynchronizationContext (or the thread pool) and could race with assertions
    /// made immediately after a synchronous call returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
