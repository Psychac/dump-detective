using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ClaimProseTests
{
    [Fact]
    public void AssertionSentence_StatesLocationMeasureAndReference_LeavingPredicateAndScopeToTheirOwnPlaces()
    {
        var assertion = new Assertion
        {
            Predicate = AssertionPredicate.Fragments,
            Measure = new Measure(22.4, MeasureUnit.Percent, MeasureSemantics.Ratio),
            Reference = new Reference { Value = new Measure(15, MeasureUnit.Percent, MeasureSemantics.Ratio), Basis = "warning threshold" },
            Qualifiers = new AssertionQualifiers { Location = "LOH/POH", Scope = "all LOH and POH segments" },
        };

        ClaimProse.AssertionSentence(assertion).Should().Be(
            "LOH/POH at 22.4%, against 15% (warning threshold).");
    }

    [Fact]
    public void AssertionSentence_FormatsBytesAndCountsByUnit_AndNeedsNoSubject()
    {
        var assertion = new Assertion
        {
            Predicate = AssertionPredicate.Exceeds,
            Measure = new Measure(1_073_741_824, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            Reference = new Reference { Value = new Measure(1_000, MeasureUnit.Count, MeasureSemantics.Count), Basis = "budget" },
        };

        ClaimProse.AssertionSentence(assertion).Should().Be("1 GB, against 1,000 (budget).");
    }

    [Fact]
    public void Build_IsNullForAClaimWithoutAnAssertion_SoUnmigratedRulesRenderAsBefore()
    {
        ClaimProse.Build(Record()).Should().BeNull();
    }

    [Fact]
    public void Build_CarriesWordingAndCounts_ButNoObservationIds()
    {
        ObservationId cited = ObservationId.NewId();
        FindingRecord record = Record() with
        {
            Assertion = new Assertion { Predicate = AssertionPredicate.Normal },
            Support = [new EvidenceStatement([cited, ObservationId.NewId()], "why one")],
            Counter = Counter.None("looked, found nothing"),
            FalsifiedBy = [new FalsificationTest { Kind = FalsificationKind.Windbg, Test = "t", Command = "!x" }],
            EvidenceSources = new EvidenceSources([["Analyzer A"]], []),
        };

        ClaimView view = ClaimProse.Build(record)!;

        ClaimEvidenceView support = view.Support.Should().ContainSingle().Subject;
        support.Why.Should().Be("why one");
        support.Observations.Should().Be(2);
        support.Sources.Should().Equal("Analyzer A");
        view.Counter!.NoneBecause.Should().Be("looked, found nothing");
        view.Tests.Should().ContainSingle().Which.Should().Be(new ClaimTestView("windbg", "t", "!x"));
    }

    [Fact]
    public void Build_TakesTheHeadlineConfidenceAndBandFromTheBreakdown_NotFromSeverity()
    {
        FindingRecord record = Record() with
        {
            Assertion = new Assertion { Predicate = AssertionPredicate.Normal },
            ConfidenceBreakdown = Breakdown(composite: 1.0),
            Confidence = 0.5,
        };

        ClaimView view = ClaimProse.Build(record)!;

        view.Predicate.Should().Be("Normal");
        view.Confidence.Should().Be(1.0);
        view.ConfidenceBand.Should().Be("high");
    }

    [Fact]
    public void ConfidenceReason_IsNullInTheTopBand()
    {
        ClaimProse.ConfidenceReason(Breakdown(composite: 0.9)).Should().BeNull();
    }

    [Fact]
    public void ConfidenceReason_BlamesTheData_WhenCapabilityFidelityIsTheBindingLimit()
    {
        string reason = ClaimProse.ConfidenceReason(Breakdown(composite: 0.5, capabilityFidelity: 0.5, factors: ["Run without an optional capability."]))!;

        reason.Should().StartWith("Limited by the data (0.50): get better data.").And.Contain("Run without an optional capability.");
    }

    [Fact]
    public void ConfidenceReason_BlamesTheInference_WhenTheConflictPenaltyIsLowest()
    {
        string reason = ClaimProse.ConfidenceReason(Breakdown(composite: 0.4, conflictPenalty: 0.4))!;

        reason.Should().StartWith("Limited by the inference (0.40)").And.Contain("use judgment");
    }

    [Fact]
    public void ConfidenceReason_BlamesTheInference_WhenARuleLoweredItsOwnCompositeAndNothingElseLimits()
    {
        ClaimProse.ConfidenceReason(Breakdown(composite: 0.6))!.Should().StartWith("Limited by the inference");
    }

    private static FindingRecord Record() =>
        new("id", "Analyzer", "Category", "Warning", "title", ["details"], "recommendation", []);

    private static ConfidenceBreakdown Breakdown(
        double composite, double capabilityFidelity = 1.0, double conflictPenalty = 1.0, IReadOnlyList<string>? factors = null) => new()
    {
        Composite = composite,
        EvidenceStrength = 1.0,
        IdentityFidelityCap = 1.0,
        TemporalAlignmentCap = 1.0,
        CapabilityFidelity = capabilityFidelity,
        ConflictPenalty = conflictPenalty,
        LimitingFactors = factors ?? [],
    };
}
