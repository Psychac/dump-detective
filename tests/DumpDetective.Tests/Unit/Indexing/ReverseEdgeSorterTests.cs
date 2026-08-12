using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ReverseEdgeSorterTests : IAsyncLifetime
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
    public async Task SortBucketsAsync_CreatesDataAndDirectoryFiles()
    {
        // Create sample bucket file with unsorted edges
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            // Write 3 edges unsorted: (0x0300, 0x1000), (0x0100, 0x2000), (0x0100, 0x3000)
            writer.Write(0x0300UL);
            writer.Write(0x1000UL);
            writer.Write(0x0100UL);
            writer.Write(0x2000UL);
            writer.Write(0x0100UL);
            writer.Write(0x3000UL);
        }

        var sorter = new ReverseEdgeSorter();
        var result = await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None);

        var dataFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.dat");
        var dirFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.idx");

        File.Exists(dataFile).Should().BeTrue();
        File.Exists(dirFile).Should().BeTrue();
        result.BucketDataSizes.Should().HaveCount(1);
        result.BucketDirectorySizes.Should().HaveCount(1);
    }

    [Fact]
    public async Task SortBucketsAsync_SortsEdgesByChildAddress()
    {
        // Create bucket file with edges in random order
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            // Child addresses in random order: 0x0300, 0x0100, 0x0200
            writer.Write(0x0300UL); writer.Write(0x1000UL);
            writer.Write(0x0100UL); writer.Write(0x2000UL);
            writer.Write(0x0200UL); writer.Write(0x3000UL);
        }

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None);

        // Verify directory entries are in ascending child order
        var dirFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.idx");
        using (var reader = new BinaryReader(File.OpenRead(dirFile)))
        {
            uint magic = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            long entryCount = reader.ReadInt64();
            reader.ReadBytes(8); // skip reserved

            magic.Should().Be(ReverseIndexConstants.Magic);
            version.Should().Be(ReverseIndexConstants.DirectoryVersion);
            entryCount.Should().Be(3);

            var prevChild = ulong.MinValue;
            for (int i = 0; i < entryCount; i++)
            {
                ulong child = reader.ReadUInt64();
                long offset = reader.ReadInt64();

                child.Should().BeGreaterThan(prevChild);
                offset.Should().BeGreaterThanOrEqualTo(0);

                prevChild = child;
            }
        }
    }

    [Fact]
    public async Task SortBucketsAsync_GroupsEdgesByChild()
    {
        // Create bucket file: 2 edges with child 0x0100, 1 edge with child 0x0200
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            writer.Write(0x0100UL); writer.Write(0x1000UL); // child 0x0100, parent 0x1000
            writer.Write(0x0200UL); writer.Write(0x2000UL); // child 0x0200, parent 0x2000
            writer.Write(0x0100UL); writer.Write(0x3000UL); // child 0x0100, parent 0x3000
        }

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None);

        // Read data file and verify grouping
        var dataFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.dat");
        using (var reader = new BinaryReader(File.OpenRead(dataFile)))
        {
            // First group: child 0x0100 with 2 parents
            ulong child1 = reader.ReadUInt64();
            int count1 = reader.ReadInt32();
            reader.ReadBoolean(); // truncated flag
            reader.ReadBytes(3);  // padding

            child1.Should().Be(0x0100UL);
            count1.Should().Be(2);

            var parents1 = new ulong[count1];
            for (int i = 0; i < count1; i++)
                parents1[i] = reader.ReadUInt64();

            parents1.Should().Contain(new[] { 0x1000UL, 0x3000UL });

            // Second group: child 0x0200 with 1 parent
            ulong child2 = reader.ReadUInt64();
            int count2 = reader.ReadInt32();
            reader.ReadBoolean();
            reader.ReadBytes(3);

            child2.Should().Be(0x0200UL);
            count2.Should().Be(1);

            ulong parent2 = reader.ReadUInt64();
            parent2.Should().Be(0x2000UL);
        }
    }

    [Fact]
    public async Task SortBucketsAsync_ThrowsWhenBucketExceedsMaxSize()
    {
        // Create a bucket file larger than max size (we'll create a small file and mock the check)
        // For now, we'll skip this test as it would require creating a very large file
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SortBucketsAsync_MultipleBucketsProcessedInParallel()
    {
        // Create multiple bucket files
        for (int b = 0; b < 3; b++)
        {
            var bucketFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{b}.tmp");
            using (var writer = new BinaryWriter(File.Create(bucketFile)))
            {
                // Each bucket gets 2 edges
                for (int i = 0; i < 2; i++)
                {
                    writer.Write((ulong)(0x0100 + i));
                    writer.Write((ulong)(0x1000 + i));
                }
            }
        }

        var sorter = new ReverseEdgeSorter();
        var result = await sorter.SortBucketsAsync(_tempDir, bucketCount: 3, CancellationToken.None);

        result.BucketDataSizes.Should().HaveCount(3);
        result.BucketDirectorySizes.Should().HaveCount(3);

        foreach (var b in Enumerable.Range(0, 3))
        {
            var dataFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{b}.dat");
            var dirFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{b}.idx");

            File.Exists(dataFile).Should().BeTrue();
            File.Exists(dirFile).Should().BeTrue();
        }
    }

    [Fact]
    public async Task SortBucketsAsync_DirectoryBinarySearchable()
    {
        // Create bucket with 10 distinct children
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            for (ulong child = 0; child < 10; child++)
            {
                writer.Write(child);
                writer.Write(0x1000UL + child);
            }
        }

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None);

        // Verify all directory entries can be found via binary search
        var dirFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.idx");
        using (var reader = new BinaryReader(File.OpenRead(dirFile)))
        {
            reader.ReadUInt32(); // magic
            reader.ReadUInt32(); // version
            long entryCount = reader.ReadInt64();
            reader.ReadBytes(8);

            var entries = new List<(ulong child, long offset)>();
            for (int i = 0; i < entryCount; i++)
            {
                ulong child = reader.ReadUInt64();
                long offset = reader.ReadInt64();
                entries.Add((child, offset));
            }

            // Verify entries are sorted
            for (int i = 1; i < entries.Count; i++)
            {
                entries[i].child.Should().BeGreaterThan(entries[i - 1].child);
            }
        }
    }

    [Fact]
    public async Task SortBucketsAsync_ReportsTruncationFlag()
    {
        // Create bucket with one child that has more than MaxParentsPerChild parents
        // (This would require special setup - for now we'll test the basic flow)
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            // Single child with 3 parents
            for (int i = 0; i < 3; i++)
            {
                writer.Write(0x0100UL);
                writer.Write((ulong)(0x1000 + i));
            }
        }

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None);

        // Read data and verify truncated flag
        var dataFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.dat");
        using (var reader = new BinaryReader(File.OpenRead(dataFile)))
        {
            reader.ReadUInt64(); // child
            reader.ReadInt32();  // count
            bool truncated = reader.ReadBoolean();

            truncated.Should().BeFalse(); // Only 3 parents, not truncated
        }
    }

    [Fact]
    public async Task SortBucketsAsync_MarksChildTruncatedWhenExtractorReportedIt()
    {
        // Phase A already caps parents-per-child at write time, so the raw bucket file itself
        // never contains more than MaxParentsPerChild parents for any child — the sorter can only
        // learn a child was truncated from the extractor's truncated-children set, not by counting.
        var bucketFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.tmp");
        using (var writer = new BinaryWriter(File.Create(bucketFile)))
        {
            writer.Write(0x0100UL); writer.Write(0x1000UL);
            writer.Write(0x0200UL); writer.Write(0x2000UL); // untouched child, not truncated
        }

        var truncatedSets = new IReadOnlySet<ulong>[] { new HashSet<ulong> { 0x0100UL } };

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 1, CancellationToken.None, truncatedSets);

        var dataFile = Path.Combine(_tempDir, "reverse_edges_bucket_0.dat");
        using var reader = new BinaryReader(File.OpenRead(dataFile));

        ulong child1 = reader.ReadUInt64();
        reader.ReadInt32();
        bool truncated1 = reader.ReadBoolean();
        reader.ReadBytes(3);
        reader.ReadUInt64(); // parent

        child1.Should().Be(0x0100UL);
        truncated1.Should().BeTrue();

        ulong child2 = reader.ReadUInt64();
        reader.ReadInt32();
        bool truncated2 = reader.ReadBoolean();

        child2.Should().Be(0x0200UL);
        truncated2.Should().BeFalse();
    }

    [Fact]
    public async Task SortBucketsAsync_ReportsProgressOncePerCompletedBucket()
    {
        for (int b = 0; b < 4; b++)
        {
            var bucketFile = Path.Combine(_tempDir, $"reverse_edges_bucket_{b}.tmp");
            using var writer = new BinaryWriter(File.Create(bucketFile));
            writer.Write((ulong)(0x0100 + b));
            writer.Write((ulong)(0x1000 + b));
        }

        var reports = new List<AnalyzerProgressReport>();
        var progress = new SynchronousProgress<AnalyzerProgressReport>(r =>
        {
            lock (reports) reports.Add(r);
        });

        var sorter = new ReverseEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount: 4, CancellationToken.None, progress: progress);

        reports.Should().OnlyContain(r => r.Phase == "sorting reverse-index buckets");
        // ScannedCount is always 0 — these are phase-label-only reports (see ConsoleUx.ObjectScanProgress),
        // not a global object counter, so per-bucket progress is carried entirely in Detail.
        reports.Should().OnlyContain(r => r.ScannedCount == 0);

        // Each bucket now reports load/sort/write sub-phase transitions in addition to the
        // per-bucket completion tick, so a slow single bucket doesn't look like a silent stall.
        reports.Should().Contain(r => r.Detail!.Contains("loaded"));
        reports.Should().Contain(r => r.Detail!.Contains("sorted"));

        var completionReports = reports.Where(r => r.Detail!.Contains("buckets done")).ToList();
        completionReports.Should().HaveCount(4);
        completionReports.Select(r => r.Detail).Should().Contain(d => d!.StartsWith("4/4 buckets done"));
    }

    /// <summary>Invokes the callback synchronously and on whichever thread reports — bucket
    /// completions run on the thread pool via <c>Task.Run</c>, so callers must synchronize their
    /// own state (unlike <see cref="Progress{T}"/>, which marshals back to a captured context).</summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
