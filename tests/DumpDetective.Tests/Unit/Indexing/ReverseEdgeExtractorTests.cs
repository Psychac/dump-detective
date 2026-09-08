using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;
using DumpDetective.Tests.Helpers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ReverseEdgeExtractorTests : IAsyncLifetime
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
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RecordEdge_WritesEdgesToCorrectBucket()
    {
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 2, _tempDir))
        {
            // Record edges for two different children
            extractor.RecordEdge(parent: 0x1000, child: 0x0100);
            extractor.RecordEdge(parent: 0x2000, child: 0x0200);
            extractor.RecordEdge(parent: 0x3000, child: 0x0100);

            await extractor.DisposeAsync();
        }

        File.Exists(ReverseEdgeBucketFileReader.BucketPath(_tempDir, 0)).Should().BeTrue();
        File.Exists(ReverseEdgeBucketFileReader.BucketPath(_tempDir, 1)).Should().BeTrue();

        // Every edge sharing a child must land in exactly one bucket — the invariant
        // ReverseEdgeCsrBuilder's lock-free counting and filling passes depend on.
        for (int bucket = 0; bucket < 2; bucket++)
        {
            foreach ((ulong child, _) in ReverseEdgeBucketFileReader.ReadBucket(_tempDir, bucket))
                ReverseIndexConstants.ChildBucketHash(child, 2).Should().Be((uint)bucket);
        }

        ReverseEdgeBucketFileReader.ReadAll(_tempDir, 2).Should().BeEquivalentTo(new[]
        {
            (Child: 0x0100UL, Parent: 0x1000UL),
            (Child: 0x0200UL, Parent: 0x2000UL),
            (Child: 0x0100UL, Parent: 0x3000UL),
        });
    }

    [Fact]
    public async Task RecordEdge_NoFanoutCap_RecordsEveryEdgeForAHubChild()
    {
        const ulong child = 0x0100;
        const int edgeCount = 10_100; // well past the old 10,000 cap

        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 1, _tempDir))
        {
            for (int i = 0; i < edgeCount; i++)
            {
                extractor.RecordEdge(parent: (ulong)(0x10000 + i), child: child);
            }

            await extractor.DisposeAsync();
        }

        // Uncapped since §4.2/§7.4 — every edge for the child is recorded, not truncated.
        List<(ulong Child, ulong Parent)> edges = ReverseEdgeBucketFileReader.ReadBucket(_tempDir, 0);
        edges.Should().HaveCount(edgeCount);
        edges.Should().OnlyContain(e => e.Child == child);
        edges.Select(e => e.Parent).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RecordEdge_MultipleChildrenSingleBucket()
    {
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 1, _tempDir))
        {
            // Record multiple children
            for (ulong child = 0; child < 10; child++)
            {
                for (int parent = 0; parent < 5; parent++)
                {
                    extractor.RecordEdge(parent: (ulong)(0x1000 + parent), child: child);
                }
            }

            await extractor.DisposeAsync();
        }

        // Should have 10 children × 5 parents each = 50 edges
        ReverseEdgeBucketFileReader.TotalEdges(_tempDir, bucketCount: 1).Should().Be(50);
        ReverseEdgeBucketFileReader.DistinctChildren(_tempDir, bucketIndex: 0).Should().Be(10);
    }

    [Fact]
    public async Task RecordEdgesBatch_WritesEveryEdgeAndClearsTheBuffer()
    {
        var buffer = new List<(ulong Child, ulong Parent)>
        {
            (0x0100UL, 0x1000UL),
            (0x0100UL, 0x2000UL),
            (0x0100UL, 0x3000UL),
        };

        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 1, _tempDir))
        {
            extractor.RecordEdgesBatch(bucketIdx: 0, buffer);
            await extractor.DisposeAsync();
        }

        buffer.Should().BeEmpty();
        ReverseEdgeBucketFileReader.ReadBucket(_tempDir, 0).Should().Equal(
            (0x0100UL, 0x1000UL),
            (0x0100UL, 0x2000UL),
            (0x0100UL, 0x3000UL));
    }

    [Fact]
    public async Task RecordEdge_ThreadSafeConcurrentAccess()
    {
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 4, _tempDir))
        {
            var tasks = new List<Task>();

            // Spawn 10 threads recording edges concurrently
            for (int thread = 0; thread < 10; thread++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (ulong i = 0; i < 100; i++)
                    {
                        extractor.RecordEdge(parent: 0x1000 + i, child: 0x0100 + i);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            await extractor.DisposeAsync();
        }

        // 10 threads × 100 edges each, with no interleaved-write corruption: a torn write would
        // leave a file length that isn't a whole number of 16-byte edges, or a child in the wrong
        // bucket, both of which the reads below would catch.
        ReverseEdgeBucketFileReader.TotalEdges(_tempDir, bucketCount: 4).Should().Be(1000);

        foreach ((ulong child, ulong parent) in ReverseEdgeBucketFileReader.ReadAll(_tempDir, 4))
        {
            child.Should().BeInRange(0x0100, 0x0163);
            parent.Should().Be(0x1000 + (child - 0x0100));
        }
    }

    [Fact]
    public async Task Dispose_FlushesAllBucketFiles()
    {
        string bucket0 = null!;

        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 1, _tempDir))
        {
            extractor.RecordEdge(parent: 0x1000, child: 0x0100);
            extractor.RecordEdge(parent: 0x2000, child: 0x0100);

            bucket0 = ReverseEdgeBucketFileReader.BucketPath(_tempDir, 0);

            await extractor.DisposeAsync();
        }

        // File should exist and contain 2 edges × 16 bytes = 32 bytes
        var fileInfo = new FileInfo(bucket0);
        fileInfo.Exists.Should().BeTrue();
        fileInfo.Length.Should().Be(32);
    }

    [Fact]
    public async Task DisposeAsync_WithProgress_ReportsOncePerBucketFlushed()
    {
        var extractor = new ReverseEdgeExtractor(bucketCount: 3, _tempDir);
        extractor.RecordEdge(parent: 0x1000, child: 0x0100);
        extractor.RecordEdge(parent: 0x2000, child: 0x0200);

        var reports = new List<AnalyzerProgressReport>();
        var progress = new SynchronousProgress<AnalyzerProgressReport>(r => reports.Add(r));

        await extractor.DisposeAsync(progress);

        reports.Should().HaveCount(3);
        reports.Should().OnlyContain(r => r.Phase == "flushing reverse-index edges");
        // ScannedCount is always 0 — these are phase-label-only reports (see ConsoleUx.ObjectScanProgress),
        // not a global object counter, so per-bucket progress is carried entirely in Detail.
        reports.Should().OnlyContain(r => r.ScannedCount == 0);
        reports.Select(r => r.Detail).Should().Equal("1/3 buckets", "2/3 buckets", "3/3 buckets");
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Fact]
    public async Task RecordEdge_BucketDistributionRoughlyUniform()
    {
        const int bucketCount = 4;
        const int edgeCount = 1000;

        await using (var extractor = new ReverseEdgeExtractor(bucketCount, _tempDir))
        {
            for (ulong i = 0; i < edgeCount; i++)
            {
                extractor.RecordEdge(parent: 0x1000, child: i);
            }

            await extractor.DisposeAsync();
        }

        // Distribution should be roughly uniform (within ±30%)
        long[] counts = ReverseEdgeBucketFileReader.EdgeCountsPerBucket(_tempDir, bucketCount);
        counts.Sum().Should().Be(edgeCount);

        long average = edgeCount / bucketCount;
        var tolerance = (uint)(average * 0.3);

        foreach (long count in counts)
        {
            count.Should().BeCloseTo(average, tolerance);
        }
    }
}
