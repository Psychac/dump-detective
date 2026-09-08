using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ThreadStackClusterTrendComparerTests
{
    private static ThreadClusterSnapshot MakeCluster(string signature, int count = 10) =>
        new(Count: count, SampleOsThreadIds: [], Signature: signature);

    private static ThreadStackClusterDomainResult MakeResult(
        int aliveThreadCount,
        IReadOnlyList<ThreadClusterSnapshot> topClusters) =>
        new(
            AliveThreadCount: aliveThreadCount,
            UniqueClusters: topClusters.Count,
            SingletonSignatures: 0,
            DiversityPercent: 50,
            TopClusterSignatures: [],
            TopClusters: topClusters);

    [Fact]
    public void Compare_AllTop5SignaturesPersist_ReturnsFullStability()
    {
        var comparer = new ThreadStackClusterTrendComparer();
        var signatures = new[] { "A", "B", "C", "D", "E" };
        var baseline = MakeResult(50, signatures.Select(s => MakeCluster(s)).ToArray());
        var current = MakeResult(50, signatures.Reverse().Select(s => MakeCluster(s)).ToArray());

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "cluster.top5.stability.percent" && d.Current == 100);
    }

    [Fact]
    public void Compare_NoTop5SignaturesPersist_ReturnsZeroStability()
    {
        var comparer = new ThreadStackClusterTrendComparer();
        var baseline = MakeResult(50, [MakeCluster("A"), MakeCluster("B")]);
        var current = MakeResult(50, [MakeCluster("C"), MakeCluster("D")]);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "cluster.top5.stability.percent" && d.Current == 0);
    }

    [Fact]
    public void Compare_PartialOverlapBeyondFive_OnlyConsidersTopFivePerSide()
    {
        var comparer = new ThreadStackClusterTrendComparer();
        var baseline = MakeResult(50, [MakeCluster("A"), MakeCluster("B"), MakeCluster("C"), MakeCluster("D"), MakeCluster("E"), MakeCluster("F")]);
        var current = MakeResult(50, [MakeCluster("A"), MakeCluster("B"), MakeCluster("C"), MakeCluster("D"), MakeCluster("G"), MakeCluster("F")]);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "cluster.top5.stability.percent" && d.Current == 80);
    }

    [Fact]
    public void Compare_BaselineHasNoTopClusters_ReturnsZeroStability()
    {
        var comparer = new ThreadStackClusterTrendComparer();
        var baseline = MakeResult(50, []);
        var current = MakeResult(50, [MakeCluster("A")]);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "cluster.top5.stability.percent" && d.Current == 0);
    }

    [Fact]
    public void Compare_NonThreadStackClusterDomainResults_ReturnsEmpty()
    {
        var comparer = new ThreadStackClusterTrendComparer();

        var deltas = comparer.Compare(new UnrelatedDomainResult(), new UnrelatedDomainResult());

        deltas.Should().BeEmpty();
    }

    private sealed record UnrelatedDomainResult : AnalyzerDomainResult;
}
