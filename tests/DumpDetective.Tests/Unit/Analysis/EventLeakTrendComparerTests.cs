using System.Linq;

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
        int scoringVersion = 2,
        int staticLeaks = 0,
        int instanceLeaks = 0) =>
        new(
            TotalEventLeakInstances: totalInstances,
            TotalSubscribers: totalSubscribers,
            StaticEventLeakCount: staticLeaks,
            InstanceEventLeakCount: instanceLeaks == 0 && staticLeaks == 0 ? totalInstances : instanceLeaks,
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

    // P0-2 regression: Compare() used to omit "event.instance.leaks" even though
    // ExtractMetrics() declares it — a regression in instance-scoped event leaks between two
    // runs was silently invisible to trend comparison.
    [Fact]
    public void Compare_SameScoringVersion_IncludesInstanceLeaksDelta()
    {
        var comparer = new EventLeakTrendComparer();
        var baseline = MakeResult(totalInstances: 5, staticLeaks: 1, instanceLeaks: 4, scoringVersion: 2);
        var current = MakeResult(totalInstances: 8, staticLeaks: 1, instanceLeaks: 7, scoringVersion: 2);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "event.instance.leaks" && d.Delta == 3);
    }

    // Guards against future asymmetry: every metric key ExtractMetrics() declares must also
    // appear in Compare()'s output (when scoring versions match), or a regression on that
    // metric becomes silently invisible to trend comparison, as happened with
    // "event.instance.leaks" (P0-2).
    [Fact]
    public void ExtractMetrics_And_Compare_DeclareTheSameMetricKeys()
    {
        var comparer = new EventLeakTrendComparer();
        var result = MakeResult(totalInstances: 3, staticLeaks: 1, instanceLeaks: 2, scoringVersion: 2);

        var extractedKeys = comparer.ExtractMetrics(result).Select(m => m.Key).ToHashSet();
        var comparedKeys = comparer.Compare(result, result).Select(d => d.Key).ToHashSet();

        comparedKeys.Should().BeEquivalentTo(extractedKeys);
    }

    private sealed record UnrelatedDomainResult : AnalyzerDomainResult;
}
