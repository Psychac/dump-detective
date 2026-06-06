using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadAnalyzerSamplerCapacityTests
{
    [Theory]
    [InlineData(200, DumpSizeTier.Small, 1000, 100)]
    [InlineData(200, DumpSizeTier.Medium, 1000, 100)]
    [InlineData(200, DumpSizeTier.Large, 1000, 50)]
    [InlineData(20, DumpSizeTier.Small, 50, 5)]
    [InlineData(0, DumpSizeTier.Small, 100, 0)]
    public void ComputeSamplerCapacity_Respects_Tier_And_ThreadCount(int maxSampled, DumpSizeTier tier, int totalThreads, int expected)
    {
        var cap = ThreadAnalyzer.ComputeSamplerCapacity(maxSampled, tier, totalThreads);
        cap.Should().Be(expected);
    }

    [Fact]
    public void ComputeSamplerCapacity_WithNullTier_Uses_ThreadCap()
    {
        var cap = ThreadAnalyzer.ComputeSamplerCapacity(50, null, 80);
        // 80 / 10 = 8
        cap.Should().Be(8);
    }
}
