using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Sdk;

/// <summary>
/// Guards docs/refactor/modularity/phase-1-sdk-review-findings.md item 11: <c>TemporalKind.Series</c>
/// was removed (it never had a valid representation on a single-artifact-scoped
/// <see cref="TemporalExtent"/>) rather than built out, and the <c>Kind</c>/<c>End</c> and
/// at-least-one-anchor-field invariants are documented, trusted-producer contracts rather than
/// runtime-enforced ones — see those types' own remarks for why. Real analyzer output
/// characterization for the same invariant lives alongside the analyzer that produces it
/// (<c>CpuHotspotAnalyzerTests</c>), not here.
/// </summary>
public sealed class TemporalTests
{
    [Fact]
    public void TemporalKind_HasNoSeriesMember()
    {
        Enum.GetValues<TemporalKind>().Should().Equal(TemporalKind.Point, TemporalKind.Interval);
    }

    [Fact]
    public void TemporalExtent_PointShape_HasNoEnd()
    {
        var point = new TemporalExtent
        {
            Kind = TemporalKind.Point,
            Start = new TimeAnchor { WallClockUtc = DateTime.UtcNow, Confidence = AnchorConfidence.Exact },
        };

        point.End.Should().BeNull();
    }

    [Fact]
    public void TemporalExtent_IntervalShape_RequiresEnd()
    {
        var interval = new TemporalExtent
        {
            Kind = TemporalKind.Interval,
            Start = new TimeAnchor { ProcessUptime = TimeSpan.Zero, Confidence = AnchorConfidence.Exact },
            End = new TimeAnchor { ProcessUptime = TimeSpan.FromSeconds(1), Confidence = AnchorConfidence.Exact },
        };

        interval.End.Should().NotBeNull();
    }

    [Fact]
    public void TimeAnchor_ValidExamples_NeedOnlyOneFieldPopulated()
    {
        var wallClockOnly = new TimeAnchor { WallClockUtc = DateTime.UtcNow, Confidence = AnchorConfidence.Approximate };
        var uptimeOnly = new TimeAnchor { ProcessUptime = TimeSpan.FromSeconds(5), Confidence = AnchorConfidence.Exact };
        var ticksOnly = new TimeAnchor { MonotonicTicks = 12345, Confidence = AnchorConfidence.Exact };

        wallClockOnly.ProcessUptime.Should().BeNull();
        uptimeOnly.WallClockUtc.Should().BeNull();
        ticksOnly.WallClockUtc.Should().BeNull();
    }
}
