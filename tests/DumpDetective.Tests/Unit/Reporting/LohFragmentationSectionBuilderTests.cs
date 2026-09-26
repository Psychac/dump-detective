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

    // The small-gap-dominance note is the rule's call now (LohFragmentationSynthesisRule; see
    // SectionNotesMovedToRulesTests) — the section only renders the histogram.
    [Fact]
    public void Build_SmallGapsDominate_LeavesTheNoteToTheRule()
    {
        LohFragmentationDomainResult result = BuildResult(
            [new FreeGapBucket("< 1 KB", 90), new FreeGapBucket("1 KB – 64 KB", 10)]);

        AnalyzerDetailSection section = new LohFragmentationSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("under 1 KB"));
        section.CompactTables.Should().Contain(t => t.Title == "Free-gap size distribution");
    }
}
