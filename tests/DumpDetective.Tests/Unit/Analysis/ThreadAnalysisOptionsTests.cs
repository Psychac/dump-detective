using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadAnalysisOptionsTests
{
    [Fact]
    public void Preset_Fast_Has_Lightweight_Caps_And_Behavioral_Knobs()
    {
        var fast = ThreadAnalysisOptions.Preset(AnalysisProfile.Fast);
        fast.MaxFramesForThreadScan.Should().Be(4);
        fast.MaxStackRootsToCount.Should().Be(128);
        fast.MaxThreadsToCaptureSnapshots.Should().Be(10);
        fast.IncludeStackSamples.Should().BeFalse();
        fast.AsyncChainDetection.Should().Be(AsyncChainDetectionMode.CountOnly);
        fast.DetectWaitPatterns.Should().BeTrue();
        fast.MaxTopHotspots.Should().Be(10);
    }

    [Fact]
    public void Preset_Full_Has_Exhaustive_Caps_And_Behavioral_Knobs()
    {
        var full = ThreadAnalysisOptions.Preset(AnalysisProfile.Full);
        full.MaxFramesForThreadScan.Should().Be(16);
        full.MaxStackRootsToCount.Should().Be(1024);
        full.MaxThreadsToCaptureSnapshots.Should().Be(50);
        full.IncludeStackSamples.Should().BeTrue();
        full.AsyncChainDetection.Should().Be(AsyncChainDetectionMode.Full);
        full.DetectWaitPatterns.Should().BeTrue();
        full.MaxTopHotspots.Should().Be(50);
    }

    [Fact]
    public void Default_Preset_Is_Balanced()
    {
        var balanced = ThreadAnalysisOptions.Preset(AnalysisProfile.Balanced);
        ThreadAnalysisOptions.Default.Should().BeEquivalentTo(balanced);
    }
}
