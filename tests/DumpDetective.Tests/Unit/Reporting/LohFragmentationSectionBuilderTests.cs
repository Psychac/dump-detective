using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class LohFragmentationSectionBuilderTests
{
    private static LohFragmentationDomainResult BuildResult(IReadOnlyList<FreeGapBucket>? histogram) =>
        new(
            SegmentCount: 1,
            TotalBytes: 10_000_000,
            FreeBytes: 1_000_000,
            UsedBytes: 9_000_000,
            FreeBlockCount: histogram is null ? 0 : histogram.Sum(b => b.GapCount),
            FragmentationPercent: 10,
            LargestFreeBlock: 500,
            FreeGapHistogram: histogram);

    private static IReadOnlyList<string> TextBlocks(AnalyzerDetailSection section) =>
        section.Blocks.OfType<TextBlock>().Select(b => b.Text).ToList();

    [Fact]
    public void Build_SmallGapsDominate_AddsInterpretationNote()
    {
        // 90 of 100 gaps are under 1 KB — above the 80% dominance threshold.
        LohFragmentationDomainResult result = BuildResult(
            [new FreeGapBucket("< 1 KB", 90), new FreeGapBucket("1 KB – 64 KB", 10)]);

        AnalyzerDetailSection section = new LohFragmentationSectionBuilder().Build(result);

        TextBlocks(section).Should().ContainSingle(t => t.Contains("under 1 KB") && t.Contains("90%"));
    }

    [Fact]
    public void Build_SmallGapsBelowThreshold_DoesNotAddInterpretationNote()
    {
        // Only 50 of 100 gaps are under 1 KB — below the 80% dominance threshold.
        LohFragmentationDomainResult result = BuildResult(
            [new FreeGapBucket("< 1 KB", 50), new FreeGapBucket("1 KB – 64 KB", 50)]);

        AnalyzerDetailSection section = new LohFragmentationSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("under 1 KB"));
    }

    [Fact]
    public void Build_NoFreeGaps_DoesNotAddInterpretationNote()
    {
        LohFragmentationDomainResult result = BuildResult(histogram: []);

        AnalyzerDetailSection section = new LohFragmentationSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("under 1 KB"));
    }

    [Fact]
    public void Build_NoSubKbBucketPresent_DoesNotAddInterpretationNote()
    {
        // All gaps are large; the "< 1 KB" bucket is entirely absent (buckets with zero count
        // are omitted from the histogram), so there is nothing to flag as sliver-dominated.
        LohFragmentationDomainResult result = BuildResult([new FreeGapBucket("1 MB – 10 MB", 5)]);

        AnalyzerDetailSection section = new LohFragmentationSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("under 1 KB"));
    }
}
