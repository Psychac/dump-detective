using DumpDetective.Analysis.Analyzers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionScanPercentTests
{
    [Theory]
    [InlineData(0, 1000, "0%")]
    [InlineData(500, 1000, "50%")]
    [InlineData(1000, 1000, "100%")]
    public void FormatScanPercent_WithTotal_ReturnsRoundedPercent(long scanned, long total, string expected)
    {
        CollectionAnalyzer.FormatScanPercent(scanned, total).Should().Be(expected);
    }

    [Fact]
    public void FormatScanPercent_ScannedExceedsTotal_ClampsAt100()
    {
        CollectionAnalyzer.FormatScanPercent(1_500, 1_000).Should().Be("100%");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void FormatScanPercent_NoUsableTotal_ReturnsNull(long? total)
    {
        CollectionAnalyzer.FormatScanPercent(100, total).Should().BeNull();
    }
}
