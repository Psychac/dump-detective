using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class MemoryAnalysisSectionBuilderTests
{
    private static MemoryDomainResult BuildResult(IReadOnlyList<SizeBucketEntry>? histogram, long totalObjects) =>
        new(
            TotalBytes: 100_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: totalObjects,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: [new TypeSnapshot("MyApp.Entry", 1_000, 100_000_000, 0)],
            SizeBucketHistogram: histogram);

    private static IReadOnlyList<string> TextBlocks(AnalyzerDetailSection section) =>
        section.Blocks.OfType<TextBlock>().Select(b => b.Text).ToList();

    [Fact]
    public void Build_HistogramUnavailableWithObjects_AddsFallbackNote()
    {
        MemoryDomainResult result = BuildResult(histogram: null, totalObjects: 1_000);

        AnalyzerDetailSection section = new MemoryAnalysisSectionBuilder().Build(result);

        TextBlocks(section).Should().ContainSingle(t =>
            t.Contains("histogram unavailable") && t.Contains("Phase-1"));
    }

    [Fact]
    public void Build_HistogramPresent_DoesNotAddFallbackNote()
    {
        MemoryDomainResult result = BuildResult(
            histogram: [new SizeBucketEntry("< 1 KB", 500, 50_000)],
            totalObjects: 1_000);

        AnalyzerDetailSection section = new MemoryAnalysisSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("histogram unavailable"));
    }

    [Fact]
    public void Build_HistogramUnavailableAndEmptyHeap_DoesNotAddFallbackNote()
    {
        MemoryDomainResult result = BuildResult(histogram: null, totalObjects: 0);

        AnalyzerDetailSection section = new MemoryAnalysisSectionBuilder().Build(result);

        TextBlocks(section).Should().NotContain(t => t.Contains("histogram unavailable"));
    }
}
