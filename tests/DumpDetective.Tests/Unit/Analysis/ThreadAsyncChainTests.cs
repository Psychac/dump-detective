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

    [Fact]
    public void BuildStackMemorySummary_Returns_Null_When_No_Samples()
    {
        ThreadAnalyzer.BuildStackMemorySummary(new List<ulong>()).Should().BeNull();
    }

    [Fact]
    public void BuildStackMemorySummary_Single_Sample_Reports_That_Value_For_All_Stats()
    {
        var summary = ThreadAnalyzer.BuildStackMemorySummary(new List<ulong> { 1_048_576 });

        summary.Should().NotBeNull();
        summary!.TotalBytes.Should().Be(1_048_576);
        summary.MeanBytes.Should().Be(1_048_576);
        summary.MaxBytes.Should().Be(1_048_576);
        summary.P95Bytes.Should().Be(1_048_576);
        summary.SampleCount.Should().Be(1);
    }

    [Fact]
    public void BuildStackMemorySummary_Computes_Total_Mean_Max_And_P95()
    {
        var samples = new List<ulong>();
        for (ulong i = 1; i <= 20; i++)
            samples.Add(i * 1_000_000);

        var summary = ThreadAnalyzer.BuildStackMemorySummary(samples);

        summary.Should().NotBeNull();
        summary!.SampleCount.Should().Be(20);
        summary.TotalBytes.Should().Be(210_000_000);
        summary.MeanBytes.Should().Be(10_500_000);
        summary.MaxBytes.Should().Be(20_000_000);
        // floor((20 - 1) * 0.95) = 18 -> zero-based index 18 -> 19th smallest sorted sample
        summary.P95Bytes.Should().Be(19_000_000);
    }
}
