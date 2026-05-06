using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadAsyncChainTests
{
    [Fact]
    public void ComputeEffectiveMaxFramesForSnapshot_Full_ExpandsWindow()
    {
        var optsFull = new ThreadAnalysisOptions { MaxFramesForThreadScan = 8, AsyncChainDetection = AsyncChainDetectionMode.Full };
        var optsFullWith = new ThreadAnalysisOptions { MaxFramesForThreadScan = 8, AsyncChainDetection = AsyncChainDetectionMode.Full };
        var optsCountOnly = new ThreadAnalysisOptions { MaxFramesForThreadScan = 8, AsyncChainDetection = AsyncChainDetectionMode.CountOnly };

        ThreadAnalyzer.ComputeEffectiveMaxFramesForSnapshot(optsFull).Should().Be(Math.Min(64, 8 * 2));
        ThreadAnalyzer.ComputeEffectiveMaxFramesForSnapshot(optsFullWith).Should().Be(Math.Min(64, 8 * 2));
        ThreadAnalyzer.ComputeEffectiveMaxFramesForSnapshot(optsCountOnly).Should().Be(8);
    }

    [Fact]
    public void CountMoveNextDepthFromSignatures_Counts_MoveNext_Occurrences()
    {
        var frames = new[] { "MyNamespace.StateMachine.MoveNext()", "OtherFrame", "AsyncRunner.MoveNext()", "nope" };
        int depth = ThreadAnalyzer.CountMoveNextDepthFromSignatures(frames);
        depth.Should().Be(2);
    }

    [Fact]
    public void ComputeEffectiveMaxFramesForSnapshot_Disabled_DoesNotExpand()
    {
        var optsDisabled = new ThreadAnalysisOptions { MaxFramesForThreadScan = 8, AsyncChainDetection = AsyncChainDetectionMode.Disabled };
        ThreadAnalyzer.ComputeEffectiveMaxFramesForSnapshot(optsDisabled).Should().Be(8);
    }
}
