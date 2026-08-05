using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class EventLeakTrendComparerTests
{
    private static EventLeakDomainResult MakeResult(
        int totalInstances = 1,
        int totalSubscribers = 1,
        int scoringVersion = 2) =>
        new(
            TotalEventLeakInstances: totalInstances,
            TotalSubscribers: totalSubscribers,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: totalInstances,
            ScoringVersion: scoringVersion);

    [Fact]
    public void Compare_SameScoringVersion_ReturnsPerMetricDeltas()
    {
        var comparer = new EventLeakTrendComparer();
        var baseline = MakeResult(totalInstances: 1, totalSubscribers: 5, scoringVersion: 2);
        var current = MakeResult(totalInstances: 2, totalSubscribers: 10, scoringVersion: 2);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().NotContain(d => d.Key == "event.leak.scoring_version_mismatch");
        deltas.Should().Contain(d => d.Key == "event.leak.instances" && d.Delta == 1);
        deltas.Should().Contain(d => d.Key == "event.total.subscribers" && d.Delta == 5);
    }

    [Fact]
    public void Compare_DifferentScoringVersion_RefusesToDiff_EmitsSingleVersionMismatchNote()
    {
        var comparer = new EventLeakTrendComparer();
        var baseline = MakeResult(scoringVersion: 1);
        var current = MakeResult(scoringVersion: 2);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().ContainSingle();
        deltas[0].Key.Should().Be("event.leak.scoring_version_mismatch");
        deltas[0].Baseline.Should().Be(1);
        deltas[0].Current.Should().Be(2);
        deltas[0].Delta.Should().Be(1);
    }

    [Fact]
    public void Compare_NonEventLeakDomainResults_ReturnsEmpty()
    {
        var comparer = new EventLeakTrendComparer();

        var deltas = comparer.Compare(new UnrelatedDomainResult(), new UnrelatedDomainResult());

        deltas.Should().BeEmpty();
    }

    private sealed record UnrelatedDomainResult : AnalyzerDomainResult;
}
