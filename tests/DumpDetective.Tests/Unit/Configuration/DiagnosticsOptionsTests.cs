using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Configuration;

public sealed class DiagnosticsOptionsTests
{
    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnFalse_ByDefault()
    {
        DiagnosticsOptions options = new();

        options.ShouldCollectAfterAnalyzerRun(1, 100, 150).Should().BeFalse();
        options.HasAnalyzerCollectionPolicy().Should().BeFalse();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_WhenLegacyFlagEnabled()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRun = true };

        options.ShouldCollectAfterAnalyzerRun(1, 100, 150).Should().BeTrue();
        options.HasAnalyzerCollectionPolicy().Should().BeTrue();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_OnInterval()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRunEveryKAnalyzers = 3 };

        options.ShouldCollectAfterAnalyzerRun(2, 100, 150).Should().BeFalse();
        options.ShouldCollectAfterAnalyzerRun(3, 100, 150).Should().BeTrue();
        options.HasAnalyzerCollectionPolicy().Should().BeTrue();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_OnWorkingSetThreshold()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRunWorkingSetThresholdBytes = 256 };

        options.ShouldCollectAfterAnalyzerRun(1, 100, 200).Should().BeFalse();
        options.ShouldCollectAfterAnalyzerRun(1, 100, 400).Should().BeTrue();
        options.ShouldCollectAfterAnalyzerRun(1, 100, 200).Should().BeFalse();
        options.ShouldCollectAfterAnalyzerRun(1, 100, 300).Should().BeTrue();
        options.HasAnalyzerCollectionPolicy().Should().BeTrue();
    }
}