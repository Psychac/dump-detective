using DumpDetective.Analysis.Pipeline;
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

        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 150).Should().BeFalse();
        AnalyzerCollectionPolicyEvaluator.HasCollectionPolicy(options).Should().BeFalse();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_WhenLegacyFlagEnabled()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRun = true };

        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 150).Should().BeTrue();
        AnalyzerCollectionPolicyEvaluator.HasCollectionPolicy(options).Should().BeTrue();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_OnInterval()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRunEveryKAnalyzers = 3 };

        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 2, 100, 150).Should().BeFalse();
        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 3, 100, 150).Should().BeTrue();
        AnalyzerCollectionPolicyEvaluator.HasCollectionPolicy(options).Should().BeTrue();
    }

    [Fact]
    public void ShouldCollectAfterAnalyzerRun_ShouldReturnTrue_OnWorkingSetThreshold()
    {
        DiagnosticsOptions options = new() { CollectAfterAnalyzerRunWorkingSetThresholdBytes = 256 };

        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 200).Should().BeFalse();
        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 400).Should().BeTrue();
        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 200).Should().BeFalse();
        AnalyzerCollectionPolicyEvaluator.ShouldCollectAfterAnalyzerRun(options, 1, 100, 300).Should().BeTrue();
        AnalyzerCollectionPolicyEvaluator.HasCollectionPolicy(options).Should().BeTrue();
    }
}