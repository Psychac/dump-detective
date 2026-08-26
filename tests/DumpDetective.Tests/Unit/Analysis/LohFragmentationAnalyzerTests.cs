using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class LohFragmentationAnalyzerTests : IDisposable
{
    private readonly string _testDir;

    public LohFragmentationAnalyzerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "loh-fragmentation-analyzer-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // ── IsLohSegment ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GCSegmentKind.Large, true)]
    [InlineData(GCSegmentKind.Pinned, true)]
    [InlineData(GCSegmentKind.Generation0, false)]
    [InlineData(GCSegmentKind.Generation1, false)]
    [InlineData(GCSegmentKind.Generation2, false)]
    [InlineData(GCSegmentKind.Frozen, false)]
    [InlineData(GCSegmentKind.Ephemeral, false)]
    public void IsLohSegment_MatchesOnlyLargeAndPinned(GCSegmentKind kind, bool expected)
    {
        LohFragmentationAnalyzer.IsLohSegment(kind).Should().Be(expected);
    }

    // ── BuildFreeGapHistogram ────────────────────────────────────────────────

    [Fact]
    public void BuildFreeGapHistogram_EmptyInput_ReturnsEmpty()
    {
        LohFragmentationAnalyzer.BuildFreeGapHistogram([]).Should().BeEmpty();
    }

    [Fact]
    public void BuildFreeGapHistogram_GroupsSizesIntoLabeledBuckets_OmittingEmptyBuckets()
    {
        List<ulong> sizes = [512, 800, 2_000, 70_000, 200_000_000];

        List<FreeGapBucket> result = LohFragmentationAnalyzer.BuildFreeGapHistogram(sizes);

        result.Should().HaveCount(4);
        result.Should().ContainSingle(b => b.GapSizeRange == "< 1 KB" && b.GapCount == 2);
        result.Should().ContainSingle(b => b.GapSizeRange == "1 KB – 64 KB" && b.GapCount == 1);
        result.Should().ContainSingle(b => b.GapSizeRange == "64 KB – 512 KB" && b.GapCount == 1);
        result.Should().ContainSingle(b => b.GapSizeRange == "≥ 100 MB" && b.GapCount == 1);
    }

    [Fact]
    public void BuildFreeGapHistogram_BucketBoundary_MinInclusiveMaxExclusive()
    {
        // 1024 is the boundary between "< 1 KB" and "1 KB - 64 KB": min is inclusive, so
        // exactly 1024 must fall into the higher bucket, not the lower one.
        List<ulong> sizes = [1_023, 1_024];

        List<FreeGapBucket> result = LohFragmentationAnalyzer.BuildFreeGapHistogram(sizes);

        result.Should().ContainSingle(b => b.GapSizeRange == "< 1 KB" && b.GapCount == 1);
        result.Should().ContainSingle(b => b.GapSizeRange == "1 KB – 64 KB" && b.GapCount == 1);
    }

    [Fact]
    public void BuildFreeGapHistogram_CancellationRequested_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => LohFragmentationAnalyzer.BuildFreeGapHistogram([1, 2, 3], cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    // ── BuildKindBreakdown ───────────────────────────────────────────────────

    [Fact]
    public void BuildKindBreakdown_SplitsLohAndPoh_WithPerKindTotalsAndLargest()
    {
        (HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)[] segments =
        [
            (HeapSegmentKind.LargeObjectHeap, 1_000_000UL, 100_000UL, 900_000UL, 50_000UL),
            (HeapSegmentKind.LargeObjectHeap, 2_000_000UL, 400_000UL, 1_600_000UL, 300_000UL),
            (HeapSegmentKind.PinnedObjectHeap, 500_000UL, 50_000UL, 450_000UL, 20_000UL),
        ];

        List<LohKindBreakdown> result = LohFragmentationAnalyzer.BuildKindBreakdown(segments);

        result.Should().HaveCount(2);

        LohKindBreakdown loh = result.Single(k => k.Kind == HeapSegmentKind.LargeObjectHeap);
        loh.SegmentCount.Should().Be(2);
        loh.TotalBytes.Should().Be(3_000_000);
        loh.FreeBytes.Should().Be(500_000);
        loh.UsedBytes.Should().Be(2_500_000);
        loh.LargestFreeBlock.Should().Be(300_000);
        loh.FragmentationPercent.Should().BeApproximately(500_000 * 100.0 / 3_000_000, 0.0001);

        LohKindBreakdown poh = result.Single(k => k.Kind == HeapSegmentKind.PinnedObjectHeap);
        poh.SegmentCount.Should().Be(1);
        poh.TotalBytes.Should().Be(500_000);
        poh.FreeBytes.Should().Be(50_000);
        poh.LargestFreeBlock.Should().Be(20_000);
    }

    [Fact]
    public void BuildKindBreakdown_EmptyInput_ReturnsEmpty()
    {
        LohFragmentationAnalyzer.BuildKindBreakdown([]).Should().BeEmpty();
    }

    // ── ReadFreeBlocks (index aggregation path) ─────────────────────────────

    [Fact]
    public void ReadFreeBlocks_AggregatesPerSegment_TracksLargestAndItsAddress()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        const ulong seg1 = 0x1000;
        const ulong seg2 = 0x9000;
        (ulong SegStart, ulong Offset, ulong Size)[] candidates =
        [
            (seg1, 0x10, 100UL),
            (seg1, 0x200, 5_000UL),
            (seg1, 0x2000, 500UL),
            (seg2, 0x50, 42UL),
        ];
        WriteContainer(containerPath, candidates);

        var bySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)>();
        var allSizes = new List<ulong>();

        LohFragmentationAnalyzer.ReadFreeBlocks(containerPath, bySegment, allSizes, CancellationToken.None);

        allSizes.Should().BeEquivalentTo([100UL, 5_000UL, 500UL, 42UL]);

        bySegment.Should().ContainKey(seg1);
        bySegment[seg1].TotalFree.Should().Be(5_600);
        bySegment[seg1].Largest.Should().Be(5_000);
        bySegment[seg1].LargestAddress.Should().Be(seg1 + 0x200);
        bySegment[seg1].Count.Should().Be(3);

        bySegment.Should().ContainKey(seg2);
        bySegment[seg2].TotalFree.Should().Be(42);
        bySegment[seg2].Largest.Should().Be(42);
        bySegment[seg2].LargestAddress.Should().Be(seg2 + 0x50);
        bySegment[seg2].Count.Should().Be(1);
    }

    [Fact]
    public void ReadFreeBlocks_NoLohFreeBlocksSection_LeavesOutputsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "cache-no-section.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.Finish();
        }

        var bySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)>();
        var allSizes = new List<ulong>();

        LohFragmentationAnalyzer.ReadFreeBlocks(containerPath, bySegment, allSizes, CancellationToken.None);

        bySegment.Should().BeEmpty();
        allSizes.Should().BeEmpty();
    }

    [Fact]
    public void ReadFreeBlocks_MissingContainer_LeavesOutputsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "does-not-exist.bin");

        var bySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)>();
        var allSizes = new List<ulong>();

        LohFragmentationAnalyzer.ReadFreeBlocks(containerPath, bySegment, allSizes, CancellationToken.None);

        bySegment.Should().BeEmpty();
        allSizes.Should().BeEmpty();
    }

    private static void WriteContainer(string containerPath, (ulong SegStart, ulong Offset, ulong Size)[] candidates)
    {
        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.LohFreeBlocks);
        long recordCount = LohFreeBlockWriter.WriteFromCandidates(writer.Stream, candidates, CancellationToken.None);
        writer.EndSection(recordCount);
        writer.Finish();
    }
}
