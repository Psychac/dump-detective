using DumpDetective.Core.Options;
using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class StringAnalyzerOptionsTests
{
    [Fact]
    public void Preset_Fast_Has_Aggressive_Sampling_And_Smaller_Caps()
    {
        var fast = StringAnalysisOptions.Preset(AnalysisProfile.Fast);
        fast.SamplingMode.Should().Be(StringSamplingMode.Aggressive);
        fast.DeduplicationMode.Should().Be(DeduplicationMode.PreferPrebuiltOnly);
        fast.MaxUniqueStringTracking.Should().BeLessThan(StringAnalysisOptions.Preset(AnalysisProfile.Balanced).MaxUniqueStringTracking);
        fast.MaxStringsToDedup.Should().BeLessThan(StringAnalysisOptions.Preset(AnalysisProfile.Balanced).MaxStringsToDedup);
    }

    [Fact]
    public void Preset_Full_Has_Full_Sampling_And_Produces_Exports()
    {
        var full = StringAnalysisOptions.Preset(AnalysisProfile.Full);
        full.SamplingMode.Should().Be(StringSamplingMode.Full);
        full.ProduceRawExports.Should().BeTrue();
        full.MaxUniqueStringTracking.Should().BeGreaterThan(StringAnalysisOptions.Preset(AnalysisProfile.Balanced).MaxUniqueStringTracking);
        full.MaxStringsToDedup.Should().BeGreaterThan(StringAnalysisOptions.Preset(AnalysisProfile.Balanced).MaxStringsToDedup);
    }

    [Theory]
    [InlineData(StringSamplingMode.Aggressive, 50000, 200000, 12500, 50000)]
    [InlineData(StringSamplingMode.Moderate, 50000, 200000, 50000, 200000)]
    [InlineData(StringSamplingMode.Full, 50000, 200000, 100000, 400000)]
    public void ComputeEffectiveCaps_Adjusts_Correctly(StringSamplingMode mode, int baseToDedup, int baseUnique, int expectedToDedup, int expectedUnique)
    {
        var opts = new StringAnalysisOptions { SamplingMode = mode };
        var (toDedup, unique) = StringAnalyzer.ComputeEffectiveCaps(opts, baseToDedup, baseUnique);
        toDedup.Should().Be(expectedToDedup);
        unique.Should().Be(expectedUnique);
    }
}
