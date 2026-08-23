using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadAsyncChainTests
{
    [Fact]
    public void CountMoveNextDepthFromSignatures_Counts_MoveNext_Occurrences()
    {
        var frames = new[] { "MyNamespace.StateMachine.MoveNext()", "OtherFrame", "AsyncRunner.MoveNext()", "nope" };
        int depth = ThreadAnalyzer.CountMoveNextDepthFromSignatures(frames);
        depth.Should().Be(2);
    }
}
