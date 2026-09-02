using DumpDetective.Analysis.Analyzers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadWaitClassifierTests
{
    [Theory]
    [InlineData("System.Threading.CountdownEvent.Wait()", "CountdownEvent")]
    [InlineData("System.Threading.CountdownEvent.Wait(Int32)", "CountdownEvent")]
    [InlineData("System.Threading.Barrier.SignalAndWait()", "Barrier")]
    [InlineData("System.Threading.Tasks.ValueTask`1[[System.Int32]].get_Result()", "TaskBlocking")]
    [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter`1[[System.Int32]].GetResult()", "TaskBlocking")]
    public void ClassifySignature_Matches_ProductionWaitPatterns(string signature, string expectedCategory)
    {
        var classification = ThreadWaitClassifier.ClassifySignature(signature, ThreadAnalyzer.WaitPatternsForTesting);

        classification.Should().NotBeNull();
        classification!.Value.Category.Should().Be(expectedCategory);
    }

    [Fact]
    public void ClassifySignature_NoMatch_ReturnsNull()
    {
        var classification = ThreadWaitClassifier.ClassifySignature("MyApp.Worker.DoWork()", ThreadAnalyzer.WaitPatternsForTesting);

        classification.Should().BeNull();
    }
}
