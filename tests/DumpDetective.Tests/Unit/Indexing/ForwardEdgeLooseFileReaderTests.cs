using DumpDetective.Analysis.Indexing.ForwardIndex;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Tests <see cref="ForwardEdgeLooseFileReader"/> against the loose <c>.dat</c>/<c>.idx</c> files
/// <see cref="ForwardEdgeSorter"/> writes directly (Phase B output) — no container involved, since
/// that's the whole point of this reader (docs/analysis/phase1-redesigns/
/// dominator-tree-phase1-integration.md §2): it's read *before*
/// <see cref="ForwardEdgeContainerWriter"/> merges those files into <c>cache.bin</c>.
/// </summary>
public class ForwardEdgeLooseFileReaderTests : IAsyncLifetime
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

    private async Task<int> BuildSortedBuckets(int bucketCount, (ulong Parent, ulong Child)[] edges)
    {
        var extractor = new ForwardEdgeExtractor(bucketCount, _tempDir);
        foreach ((ulong parent, ulong child) in edges)
            extractor.RecordEdge(parent, child);
        await extractor.DisposeAsync();

        var sorter = new ForwardEdgeSorter();
        await sorter.SortBucketsAsync(_tempDir, bucketCount, CancellationToken.None);

        return bucketCount;
    }

    [Fact]
    public async Task GetChildren_RoundTripsExactChildSetPerParent()
    {
        (ulong Parent, ulong Child)[] edges =
        [
            (0x1000UL, 0x0100UL),
            (0x1000UL, 0x0200UL),
            (0x1000UL, 0x0300UL),
            (0x2000UL, 0x0400UL),
        ];

        int bucketCount = await BuildSortedBuckets(4, edges);

        ForwardEdgeLooseFileReader.TryOpen(_tempDir, bucketCount, out ForwardEdgeLooseFileReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            var buffer = new ulong[4];

            int count1000 = reader!.GetChildren(0x1000UL, ref buffer);
            buffer[..count1000].Should().BeEquivalentTo([0x0100UL, 0x0200UL, 0x0300UL]);

            int count2000 = reader.GetChildren(0x2000UL, ref buffer);
            buffer[..count2000].Should().BeEquivalentTo([0x0400UL]);
        }
    }

    [Fact]
    public async Task GetChildren_UnknownParent_ReturnsZero()
    {
        (ulong Parent, ulong Child)[] edges = [(0x1000UL, 0x0100UL)];
        int bucketCount = await BuildSortedBuckets(2, edges);

        ForwardEdgeLooseFileReader.TryOpen(_tempDir, bucketCount, out ForwardEdgeLooseFileReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            // 0x0100UL is a leaf — recorded as a child, never as a parent.
            var buffer = new ulong[4];
            int count = reader!.GetChildren(0x0100UL, ref buffer);
            count.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetChildren_GrowsBufferForHighFanoutParent()
    {
        const int childCount = 500;
        var edges = new (ulong Parent, ulong Child)[childCount];
        for (int i = 0; i < childCount; i++)
            edges[i] = (0xAAAAUL, (ulong)(0x1000 + i));

        int bucketCount = await BuildSortedBuckets(2, edges);

        ForwardEdgeLooseFileReader.TryOpen(_tempDir, bucketCount, out ForwardEdgeLooseFileReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            var buffer = new ulong[4]; // deliberately smaller than childCount — exercises resize
            int count = reader!.GetChildren(0xAAAAUL, ref buffer);

            count.Should().Be(childCount);
            buffer.Length.Should().BeGreaterThanOrEqualTo(childCount);
            buffer[..count].Should().BeEquivalentTo(edges.Select(e => e.Child));
        }
    }

    [Fact]
    public async Task TryOpen_EmptyBucket_TreatedAsZeroEntriesNotFailure()
    {
        // A single edge with many buckets requested — most buckets never get a .dat/.idx pair
        // written at all (ForwardEdgeSorter never creates files for a bucket with zero edges).
        (ulong Parent, ulong Child)[] edges = [(0x1000UL, 0x0100UL)];
        int bucketCount = await BuildSortedBuckets(16, edges);

        ForwardEdgeLooseFileReader.TryOpen(_tempDir, bucketCount, out ForwardEdgeLooseFileReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            var buffer = new ulong[4];
            // 0xDEADBEEFUL was never recorded as a parent — regardless of which bucket it hashes
            // to (empty or not), this must return 0, not throw.
            int count = reader!.GetChildren(0xDEADBEEFUL, ref buffer);
            count.Should().Be(0);
        }
    }
}
