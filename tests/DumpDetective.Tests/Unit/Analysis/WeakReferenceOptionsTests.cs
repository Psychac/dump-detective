using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class WeakReferenceOptionsTests
{
    [Fact]
    public void Preset_Fast_Maps_To_Smaller_Caps_And_Small_Probe()
    {
        var fast = WeakReferenceAnalysisOptions.Preset(AnalysisProfile.Fast);
        fast.HandleScanCap.Should().Be(20_000);
        fast.TopTypeLimit.Should().Be(8);
        fast.WeakRefProbeSampleLimit.Should().Be(4);
        fast.ProduceRawExports.Should().BeFalse();
    }

    [Fact]
    public void Preset_Balanced_Is_Default()
    {
        var bal = WeakReferenceAnalysisOptions.Preset(AnalysisProfile.Balanced);
        bal.HandleScanCap.Should().Be(50_000);
        bal.TopTypeLimit.Should().Be(15);
        bal.WeakRefProbeSampleLimit.Should().Be(8);
        bal.ProduceRawExports.Should().BeFalse();
    }

    [Fact]
    public void Preset_Full_Enables_Exports_And_Large_Probe()
    {
        var full = WeakReferenceAnalysisOptions.Preset(AnalysisProfile.Full);
        full.HandleScanCap.Should().Be(200_000);
        full.TopTypeLimit.Should().Be(40);
        full.WeakRefProbeSampleLimit.Should().Be(32);
        full.ProduceRawExports.Should().BeTrue();
    }
}
