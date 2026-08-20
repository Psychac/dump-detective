using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

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

        // Verify bucket files exist
        var bucket0 = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        var bucket1 = Path.Combine(_tempDir, "reverse_edges_bucket_1.tmp");

        File.Exists(bucket0).Should().BeTrue();
        File.Exists(bucket1).Should().BeTrue();
    }

    [Fact]
    public async Task RecordEdge_NoFanoutCap_RecordsEveryEdgeForAHubChild()
    {
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 1, _tempDir))
        {
            const ulong child = 0x0100;
            const int edgeCount = 10_100; // well past the old 10,000 cap

            for (int i = 0; i < edgeCount; i++)
            {
                extractor.RecordEdge(parent: (ulong)(0x10000 + i), child: child);
            }

            var stats = extractor.GetStatistics();

            await extractor.DisposeAsync();

            // Uncapped since §4.2/§7.4 — every edge for the child is recorded, not truncated.
            stats.BucketStats[0].EdgeCount.Should().Be(edgeCount);
        }
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

            var stats = extractor.GetStatistics();

            await extractor.DisposeAsync();

            // Should have 10 children × 5 parents each = 50 edges
            stats.TotalEdgesRecorded.Should().Be(50);
            stats.BucketStats[0].UniqueChildrenCount.Should().Be(10);
        }
    }

    [Fact]
    public async Task GetStatistics_ReflectsRecordedEdges()
    {
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 4, _tempDir))
        {
            // Record 100 edges across different buckets
            for (int i = 0; i < 100; i++)
            {
                extractor.RecordEdge(parent: (ulong)(0x2000 + i), child: (ulong)(0x0100 + i));
            }

            var stats = extractor.GetStatistics();

            await extractor.DisposeAsync();

            stats.BucketCount.Should().Be(4);
            stats.TotalEdgesRecorded.Should().Be(100);
            stats.BucketStats.Should().HaveCount(4);
            stats.BucketStats.Sum(s => s.EdgeCount).Should().Be(100);
        }
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
            var stats = extractor.GetStatistics();

            await extractor.DisposeAsync();

            stats.TotalEdgesRecorded.Should().Be(1000); // 10 threads × 100 edges each
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

            bucket0 = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");

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
        await using (var extractor = new ReverseEdgeExtractor(bucketCount: 4, _tempDir))
        {
            // Record 1000 edges
            for (ulong i = 0; i < 1000; i++)
            {
                extractor.RecordEdge(parent: 0x1000, child: i);
            }

            var stats = extractor.GetStatistics();

            await extractor.DisposeAsync();

            // Distribution should be roughly uniform (within ±30%)
            var average = stats.TotalEdgesRecorded / stats.BucketCount;
            var tolerance = average * 0.3;

            foreach (var bucket in stats.BucketStats)
            {
                bucket.EdgeCount.Should().BeCloseTo(average, (uint)tolerance);
            }
        }
    }
}
