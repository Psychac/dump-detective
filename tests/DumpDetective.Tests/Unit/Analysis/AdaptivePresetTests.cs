using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class AdaptivePresetTests
{
    [Fact]
    public void AdaptForSize_Applies_AdaptiveFactor()
    {
        var opts = new ThreadAnalysisOptions { MaxThreadsToCaptureSnapshots = 100, MaxSampledStackSnapshots = 100 };
        var adapted = ThreadAnalysisOptions.AdaptForSize(opts, DumpSizeTier.Small);
        adapted.MaxThreadsToCaptureSnapshots.Should().Be(100);
        adapted.MaxSampledStackSnapshots.Should().Be(100);
    }

    [Fact]
    public void AdaptForSize_Scales_By_Tier_Only()
    {
        var opts = new ThreadAnalysisOptions { MaxThreadsToCaptureSnapshots = 200, MaxSampledStackSnapshots = 200 };
        // Large tier divides by 4 -> 50
        var adapted = ThreadAnalysisOptions.AdaptForSize(opts, DumpSizeTier.Large);
        adapted.MaxThreadsToCaptureSnapshots.Should().Be(50);
        adapted.MaxSampledStackSnapshots.Should().Be(50);
    }
}
