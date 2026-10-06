using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Section confidence badges come from their findings' measured limitations (Phase 5 exit
/// criterion: section builders are presentation-only). The rules apply each data-quality flag to
/// the findings whose values it affects; the report assembler turns those into the badge.
/// </summary>
public sealed class SectionConfidenceFromFindingsTests
{
    [Theory]
    [InlineData(0.01, 0.65, "Sampling coverage is below 5% (1.0%)")]
    [InlineData(0.20, 0.85, "Sampling coverage is 20.0%")]
    [InlineData(0.80, 1.0, null)]
    [InlineData(0.0, 1.0, null)]
    public async Task StringRule_SamplingCoverageLowersOnlyTheDuplicationFindings(double coverage, double expectedComposite, string? expectedFactor)
    {
        IReadOnlyList<Claim> findings = await new StringSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([MakeStringComposition(coverage)]), new SynthesisContext());

        Claim duplication = findings.Single(f => f.Fingerprint.EndsWith("extreme-duplication", StringComparison.Ordinal));
        duplication.Confidence.Composite.Should().BeApproximately(expectedComposite, 1e-12);
        if (expectedFactor is null)
            duplication.Confidence.LimitingFactors.Should().BeEmpty();
        else
            duplication.Confidence.LimitingFactors.Should().ContainSingle().Which.Should().StartWith(expectedFactor);

        findings.Single(f => f.Fingerprint.EndsWith("very-long-strings", StringComparison.Ordinal))
            .Confidence.Should().Be(ConfidenceBreakdown.MeasurementCertain, "very-long-string figures are exact, not sampled");
    }

    [Fact]
    public void DataQualityPenalties_SubtractsAppliedPenalties_FloorsAtPointTwo()
    {
        ConfidenceBreakdown result = DataQualityPenalties.Apply(ConfidenceBreakdown.MeasurementCertain,
            new DataQualityPenalty(true, 0.5, "a"),
            new DataQualityPenalty(false, 0.4, "not applied"),
            new DataQualityPenalty(true, 0.5, "b"));

        result.Composite.Should().Be(0.2);
        result.LimitingFactors.Should().Equal("a", "b");
    }

    [Fact]
    public void Assembler_DerivesBadgeFromLeastConfidentLimitedFinding()
    {
        Claim limited = MakeFinding("Duplication", 0.65, ["Sampling coverage is below 5% (1.0%)."]);
        Claim alsoLimited = MakeFinding("Other", 0.85, ["Sampling coverage is 20.0%."]);
        Claim unlimited = MakeFinding("Exact", null, []);
        List<AnalyzerDetailSection> sections = [new AnalyzerDetailSection("String Analysis", "Strings", 0, [])];
        AnalyzerRunResult run = MakeRun([limited, alsoLimited, unlimited]);

        ReportSectionAssembler.NormalizeSectionContractSlots(sections, [run]);

        ConfidenceBandBlock band = sections[0].Blocks.OfType<ConfidenceBandBlock>().Should().ContainSingle().Subject;
        band.Score.Should().Be(0.65);
        band.Caveats.Should().Equal("Sampling coverage is below 5% (1.0%).", "Sampling coverage is 20.0%.");
        band.IsLowestFindingConfidence.Should().BeTrue();
        band.Label.Should().Be("Lowest finding confidence: Med-High");
    }

    [Fact]
    public void Assembler_NoBadge_WhenNoFindingIsLimited()
    {
        List<AnalyzerDetailSection> sections = [new AnalyzerDetailSection("String Analysis", "Strings", 0, [])];

        ReportSectionAssembler.NormalizeSectionContractSlots(sections, [MakeRun([MakeFinding("Exact", null, [])])]);

        sections[0].Blocks.OfType<ConfidenceBandBlock>().Should().BeEmpty();
    }

    [Fact]
    public void Assembler_KeepsABuilderStatedBadge()
    {
        var builderBadge = new ConfidenceBandBlock("High", 0.85, "●●●●", ["stated by the builder"]);
        List<AnalyzerDetailSection> sections = [new AnalyzerDetailSection("String Analysis", "Strings", 0, [builderBadge])];

        ReportSectionAssembler.NormalizeSectionContractSlots(sections, [MakeRun([MakeFinding("Limited", 0.5, ["x"])])]);

        sections[0].Blocks.OfType<ConfidenceBandBlock>().Should().ContainSingle().Which.Should().BeSameAs(builderBadge);
        builderBadge.Label.Should().Be("High confidence");
    }

    [Fact]
    public async Task DominatorRule_ReferenceCountingLimitsLowerTheHotspot_AndTopTypeIsAlwaysAnEstimate()
    {
        IReadOnlyList<Claim> findings = await new DominatorSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("type.retention", "Dominator Analysis", new()
                {
                    ["instance.count"] = Count(10), ["shallow.bytes"] = Bytes(1_000), ["retained.bytes"] = Bytes(5_000),
                    ["estimated-retained.bytes"] = Bytes(5_000), ["gen2.count"] = Count(0), ["loh.bytes"] = Bytes(0),
                }, typeName: "App.Top"),
                MakeObservation("type.retention-population", "Dominator Analysis", new()
                {
                    ["highly-referenced.count"] = Count(0),
                    ["approximated-reference-addresses.count"] = Count(0),
                    ["is-object-scan-capped"] = Count(1),
                    ["is-reference-counting-skipped"] = Count(0),
                }),
                MakeObservation("type.retention-hotspot", "Dominator Analysis", new()
                {
                    ["object.count"] = Count(10), ["total.bytes"] = Bytes(1_000), ["total-incoming-references.count"] = Count(5_000),
                }, typeName: "App.Hot"),
            ]),
            new SynthesisContext());

        Claim hotspot = findings.Single(f => f.Fingerprint.EndsWith("dominant-retained-type-hotspot", StringComparison.Ordinal));
        hotspot.Confidence.Composite.Should().BeApproximately(0.85, 1e-12);
        hotspot.Confidence.LimitingFactors.Should().Equal("Object scan was capped; retention counts may be partial.");

        Claim top = findings.Single(f => f.Fingerprint.EndsWith("top-dominator-type", StringComparison.Ordinal));
        top.Confidence.Composite.Should().Be(0.7);
    }

    [Theory]
    [InlineData(true, false, 1.0)]
    [InlineData(false, false, 0.7)]
    [InlineData(false, true, 0.5)]
    [InlineData(true, true, 1.0)]
    public async Task GCRootRule_StaticRetentionConfidence_FollowsExactnessAndWalkCap(bool exact, bool walkCapped, double expected)
    {
        IReadOnlyList<Claim> findings = await new GCRootSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("root.population", "GC Root Analysis", new()
                {
                    ["total.count"] = Count(10), ["subgraph-walk-capped"] = Count(walkCapped ? 1 : 0), ["subgraph-walk-capped.count"] = Count(walkCapped ? 3 : 0),
                    ["strong-handle.count"] = Count(5), ["finalizer-queue.count"] = Count(0),
                }),
                MakeObservation("root.kind-summary", "GC Root Analysis", new()
                {
                    ["kind.ordinal"] = new Measure((int)HeapRootKind.StrongHandle, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["count"] = Count(5), ["retained.bytes"] = Bytes(50_000_000), ["is-retained-exact"] = Count(exact ? 1 : 0),
                }),
            ]),
            new SynthesisContext());

        findings.Single(f => f.Fingerprint.EndsWith("static-root-retention", StringComparison.Ordinal))
            .Confidence.Composite.Should().BeApproximately(expected, 1e-12);
    }

    private static Claim MakeFinding(string title, double? score, IReadOnlyList<string> limitingFactors)
    {
        Claim claim = ClaimFactory.Create("String Analysis", "Memory", Severity.Info, title, "evidence", "recommendation", [], confidenceScore: score);
        return claim with { Confidence = claim.Confidence with { LimitingFactors = limitingFactors } };
    }

    private static AnalyzerRunResult MakeRun(IReadOnlyList<Claim> findings) => new(
        AnalyzerName: "String Analysis",
        Status: AnalyzerExecutionStatus.Success,
        Duration: TimeSpan.Zero,
        Result: null,
        ErrorMessage: null,
        ErrorType: null,
        Findings: findings);

    private static Observation MakeObservation(string observationType, string analyzerKey, Dictionary<string, Measure> measures, string? typeName = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = typeName is null ? [] : [new DumpDetective.Sdk.Identity.TypeRef { CanonicalName = typeName, Fidelity = DumpDetective.Sdk.Identity.MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("section-confidence-test"),
            AnalyzerKey = analyzerKey,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static Observation MakeStringComposition(double samplingCoverage) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "type.string-composition",
        Subjects = [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>
        {
            ["duplication.ratio"] = Ratio(0.9),
            ["duplicate-pattern.count"] = Count(100),
            ["duplicate-wasted.bytes"] = Bytes(50_000_000),
            ["loh-string.bytes"] = Bytes(0),
            ["very-long-string.count"] = Count(5),
            ["very-long-string.bytes"] = Bytes(10_000_000),
            ["pct-of-managed-heap.percent"] = new Measure(5, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["total-string-memory.bytes"] = Bytes(100_000_000),
            ["sampling-coverage.ratio"] = Ratio(samplingCoverage),
            ["strings-sampled.count"] = Count(1_000),
            ["total-string.count"] = Count(100_000),
            ["unique-pattern.count"] = Count(100),
        },
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("section-confidence-test"),
            AnalyzerKey = "String Analysis",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Absolute);
    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);
    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);
}
