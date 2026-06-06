using DumpDetective.Core.Options;
using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Unit.Analysis;

public class PresetBehaviorTests
{
    [Fact]
    public void Fast_Preset_Disables_Sampling()
    {
        var fast = ThreadAnalysisOptions.Preset(AnalysisProfile.Fast);
        fast.IncludeStackSamples.Should().BeFalse();
        // MaxSampledStackSnapshots is zero for Fast; capacity should be zero regardless of tier
        int cap = ThreadAnalyzer.ComputeSamplerCapacity(fast.MaxSampledStackSnapshots, DumpSizeTier.Small, totalThreads: 100);
        cap.Should().Be(0);
    }

    [Fact]
    public void Balanced_Preset_Enables_Sampling()
    {
        var balanced = ThreadAnalysisOptions.Default; // Balanced
        balanced.IncludeStackSamples.Should().BeTrue();
        var cap = ThreadAnalyzer.ComputeSamplerCapacity(balanced.MaxSampledStackSnapshots, DumpSizeTier.Small, totalThreads: 100);
        cap.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Full_Preset_Uses_Background_Prewarm()
    {
        var full = ThreadAnalysisOptions.Preset(AnalysisProfile.Full);
        full.PrewarmCacheInBackground.Should().BeTrue();
    }
}
