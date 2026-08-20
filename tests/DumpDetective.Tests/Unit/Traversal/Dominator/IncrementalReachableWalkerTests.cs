using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// §4.1 prototype correctness tests — mirrors <c>ReachableGraphWalkerTests</c>'s synthetic graphs.
/// No disk-backed object index needed (v3 dropped the ordinal/<c>ObjectAddressLookup</c> dependency
/// in favor of plain <see cref="HashSet{T}"/>-based visited-tracking), only a scratch directory for
/// <see cref="ReverseEdgeExtractor"/>'s bucket files.
/// </summary>
public sealed class IncrementalReachableWalkerTests : IDisposable
{
    private readonly string _testDir;

    public IncrementalReachableWalkerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "incremental-walker-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void Walk_DiamondGraph_MatchesReachableGraphWalkerNodeAndEdgeCounts()
    {
        // root(0x10) -> a(0x20), root(0x10) -> b(0x30), a(0x20) -> c(0x40), b(0x30) -> c(0x40)
        var successors = SyntheticSuccessors.Build(
            (0x10UL, 0x20UL), (0x10UL, 0x30UL), (0x20UL, 0x40UL), (0x30UL, 0x40UL));

        var extractor = new ReverseEdgeExtractor(bucketCount: 1, _testDir);
        IncrementalReachableWalker.Result result;
        try
        {
            result = IncrementalReachableWalker.Walk([0x10UL], successors, extractor, CancellationToken.None);
        }
        finally
        {
            extractor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        result.NodeCount.Should().Be(4);
        result.EdgeCount.Should().Be(4);
    }

    [Fact]
    public void Walk_MultipleRoots_AllReachableFromEitherRoot()
    {
        var successors = SyntheticSuccessors.Build((0x1UL, 0x3UL), (0x2UL, 0x3UL));

        var extractor = new ReverseEdgeExtractor(bucketCount: 1, _testDir);
        IncrementalReachableWalker.Result result;
        try
        {
            result = IncrementalReachableWalker.Walk([0x1UL, 0x2UL], successors, extractor, CancellationToken.None);
        }
        finally
        {
            extractor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        result.NodeCount.Should().Be(3);
        result.EdgeCount.Should().Be(2);
    }

    [Fact]
    public void Walk_StreamsEdgesToReverseEdgeExtractor_RecoverableAfterSortAndMerge()
    {
        // Same diamond as above — verifies the streamed edges survive Phase B (sort) exactly like
        // the existing reverse-edge index's own edges do, since this prototype reuses that pipeline
        // unmodified downstream of the walk itself.
        var successors = SyntheticSuccessors.Build(
            (0x10UL, 0x20UL), (0x10UL, 0x30UL), (0x20UL, 0x40UL), (0x30UL, 0x40UL));

        var extractor = new ReverseEdgeExtractor(bucketCount: 1, _testDir);
        IncrementalReachableWalker.Walk([0x10UL], successors, extractor, CancellationToken.None);

        ReverseEdgeExtractionStats stats = extractor.GetStatistics();
        extractor.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var sorter = new ReverseEdgeSorter();
        sorter.SortBucketsAsync(_testDir, bucketCount: 1, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Phase C — a fresh container just for this merge; unrelated to any object-index container.
        string reverseIndexContainerPath = Path.Combine(_testDir, "reverse-index.bin");
        using (var reverseWriter = new CacheContainerWriter(reverseIndexContainerPath))
        {
            ReverseEdgeContainerWriter.Write(reverseWriter, _testDir, bucketCount: 1, stats, progress: null);
            reverseWriter.Finish();
        }

        CacheContainerReader.TryOpen(reverseIndexContainerPath, out CacheContainerReader? containerReader)
            .Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(containerReader!, out ReverseEdgeIndexReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            reader!.TryGetParents(0x40UL, out IReadOnlyList<ulong> parentsOfC, out bool truncatedFlag)
                .Should().BeTrue();
            truncatedFlag.Should().BeFalse();
            parentsOfC.Should().BeEquivalentTo([0x20UL, 0x30UL]);
        }
    }

    [Fact]
    public void Walk_UnreachableSubgraph_GetsNoReverseIndexEntries()
    {
        // §7.3 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the
        // reverse-edge index is now built by this walk instead of a raw per-object field scan, so
        // it only ever sees edges the BFS actually crosses. 0x99 -> 0xAA models a garbage
        // subgraph — nothing points to 0x99 and it's not a root, so the walk never visits it, and
        // 0xAA (which would otherwise look identical to a reachable child) must end up with no
        // recorded parents at all, not an empty-but-present entry.
        var successors = SyntheticSuccessors.Build(
            (0x10UL, 0x20UL), (0x99UL, 0xAAUL));

        var extractor = new ReverseEdgeExtractor(bucketCount: 1, _testDir);
        IncrementalReachableWalker.Result result =
            IncrementalReachableWalker.Walk([0x10UL], successors, extractor, CancellationToken.None);

        result.NodeCount.Should().Be(2); // root 0x10 and reachable child 0x20 only
        result.EdgeCount.Should().Be(1); // only 0x10 -> 0x20 was ever crossed

        ReverseEdgeExtractionStats stats = extractor.GetStatistics();
        extractor.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var sorter = new ReverseEdgeSorter();
        sorter.SortBucketsAsync(_testDir, bucketCount: 1, CancellationToken.None)
            .GetAwaiter().GetResult();

        string reverseIndexContainerPath = Path.Combine(_testDir, "reverse-index-unreachable.bin");
        using (var reverseWriter = new CacheContainerWriter(reverseIndexContainerPath))
        {
            ReverseEdgeContainerWriter.Write(reverseWriter, _testDir, bucketCount: 1, stats, progress: null);
            reverseWriter.Finish();
        }

        CacheContainerReader.TryOpen(reverseIndexContainerPath, out CacheContainerReader? containerReader)
            .Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(containerReader!, out ReverseEdgeIndexReader? reader)
            .Should().BeTrue();
        using (reader)
        {
            reader!.TryGetParents(0x20UL, out IReadOnlyList<ulong> parentsOf20, out _)
                .Should().BeTrue();
            parentsOf20.Should().BeEquivalentTo([0x10UL]);

            reader!.TryGetParents(0xAAUL, out IReadOnlyList<ulong> parentsOfGarbage, out _)
                .Should().BeFalse();
            parentsOfGarbage.Should().BeEmpty();
        }
    }
}
