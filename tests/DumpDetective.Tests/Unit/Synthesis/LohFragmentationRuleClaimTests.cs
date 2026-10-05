using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class LohFragmentationRuleClaimTests
{
    private const string Analyzer = "LOH & POH Fragmentation Analysis";

    [Fact]
    public async Task ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LohFragmentationSynthesisRule(),
        [
            new("warning-with-reusable-gaps", [SegmentRow(totalBytes: 1_000_000, freeBytes: 224_000), Summary(subKbGaps: 40)]),
            new("critical-sliver-dominated", [SegmentRow(totalBytes: 1_000_000, freeBytes: 350_000), Summary(subKbGaps: 90)]),
            new("info-without-summary", [SegmentRow(totalBytes: 1_000_000, freeBytes: 20_000)]),
            new("info-sliver-dominated", [SegmentRow(totalBytes: 1_000_000, freeBytes: 20_000), Summary(subKbGaps: 90)]),
        ]);
    }

    [Theory]
    [InlineData(224_000, AssertionPredicate.Fragments, 15.0)]
    [InlineData(350_000, AssertionPredicate.Fragments, 30.0)]
    [InlineData(20_000, AssertionPredicate.Normal, 15.0)]
    public async Task Assertion_UsesTheCrossedThresholdAsItsReference(long freeBytes, AssertionPredicate predicate, double budget)
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, freeBytes)]);

        claim.Assertion.Should().NotBeNull();
        claim.Assertion!.Predicate.Should().Be(predicate);
        claim.Assertion.Measure!.Value.Value.Should().BeApproximately(freeBytes * 100.0 / 1_000_000, 0.001);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
    }

    [Fact]
    public async Task Support_CitesSegmentRowsAndSummary_AndEveryIdIsInTheLineage()
    {
        Observation row = SegmentRow(1_000_000, 224_000);
        Observation summary = Summary(subKbGaps: 40);

        Claim claim = await SynthesizeSingleAsync([row, summary]);

        claim.Support.Should().HaveCount(2);
        claim.Support[0].Observations.Should().Equal(row.Id);
        claim.Support[1].Observations.Should().Equal(summary.Id);
        claim.Support.SelectMany(s => s.Observations).Should().OnlyContain(id => claim.DerivedFrom.Contains(id));
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(81, true)]
    public async Task Counter_IsEvidenceWhenGapsAreReusable_AndNoneWhenSliverDominated(int subKbGaps, bool expectNone)
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, 224_000), Summary(subKbGaps)]);

        claim.Counter.Should().NotBeNull();
        if (expectNone)
        {
            claim.Counter!.Statements.Should().BeEmpty();
            claim.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            claim.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain($"{subKbGaps}%");
            claim.Counter.NoneFoundBecause.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(40, false)]
    public async Task Counter_ForANormalClaim_PointsAtSliversNotAtReusableGaps(int subKbGaps, bool expectEvidence)
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, 20_000), Summary(subKbGaps)]);

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        if (expectEvidence)
            claim.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain($"{subKbGaps}% of free gaps are under 1 KB");
        else
            claim.Counter!.NoneFoundBecause.Should().Contain("mostly large enough to be reused");
    }

    [Fact]
    public async Task Counter_StatesWhatCouldNotBeChecked_WhenThereIsNoSummary()
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, 224_000)]);

        claim.Counter!.NoneFoundBecause.Should().Contain("No free-block distribution");
    }

    [Theory]
    [InlineData(224_000, 2)]
    [InlineData(20_000, 1)]
    public async Task FalsifiedBy_NamesAConcreteTest_AndCaptureOnlyForAProblem(long freeBytes, int expectedCount)
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, freeBytes)]);

        claim.FalsifiedBy.Should().HaveCount(expectedCount);
        claim.FalsifiedBy[0].Kind.Should().Be(FalsificationKind.Windbg);
        claim.FalsifiedBy[0].Command.Should().Be("!dumpheap -type Free -stat");
    }

    [Fact]
    public async Task ExistingProse_IsUnchangedByTheStructuredFields()
    {
        Claim claim = await SynthesizeSingleAsync([SegmentRow(1_000_000, 224_000)]);

        claim.Title.Should().Be("Elevated LOH/POH fragmentation — 22.4% free-space fragmentation");
        claim.Severity.Should().Be(Severity.Warning);
    }

    private static async Task<Claim> SynthesizeSingleAsync(IReadOnlyList<Observation> observations)
    {
        IReadOnlyList<Claim> claims = await new LohFragmentationSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());
        return claims.Should().ContainSingle().Subject;
    }

    private static Observation SegmentRow(long totalBytes, long freeBytes) =>
        RuleShapeGolden.Observation("heap.loh-fragmentation", Analyzer, new()
        {
            ["segment.count"] = new Measure(2, MeasureUnit.Count, MeasureSemantics.Count),
            ["total.bytes"] = new Measure(totalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["free.bytes"] = new Measure(freeBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
        });

    private static Observation Summary(int subKbGaps) =>
        RuleShapeGolden.Observation("heap.loh-fragmentation-summary", Analyzer, new()
        {
            ["free-block.count"] = new Measure(100, MeasureUnit.Count, MeasureSemantics.Count),
            ["largest-free-block.bytes"] = new Measure(4_096, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["free-gap.count"] = new Measure(100, MeasureUnit.Count, MeasureSemantics.Count),
            ["sub-kb-free-gap.count"] = new Measure(subKbGaps, MeasureUnit.Count, MeasureSemantics.Count),
        });
}
