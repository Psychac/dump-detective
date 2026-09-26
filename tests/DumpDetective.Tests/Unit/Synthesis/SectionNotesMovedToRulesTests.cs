using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Notes that used to be threshold-gated text in section builders, now in their rules' narratives
/// (Phase 5 exit criterion: section builders are presentation-only).
/// </summary>
public sealed class SectionNotesMovedToRulesTests
{
    [Theory]
    [InlineData(81, true)]
    [InlineData(80, false)]
    [InlineData(0, false)]
    public async Task LohFragmentationRule_NotesSmallGapDominance_AboveEightyPercent(int subKbGaps, bool expectNote)
    {
        IReadOnlyList<Finding> findings = await new LohFragmentationSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("heap.loh-fragmentation", "LOH & POH Fragmentation Analysis", new()
                {
                    ["segment.count"] = Count(2),
                    ["total.bytes"] = new Measure(1_000_000, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["free.bytes"] = new Measure(10_000, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                }),
                MakeObservation("heap.loh-fragmentation-summary", "LOH & POH Fragmentation Analysis", new()
                {
                    ["free-block.count"] = Count(100),
                    ["largest-free-block.bytes"] = new Measure(4_096, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["free-gap.count"] = Count(100),
                    ["sub-kb-free-gap.count"] = Count(subKbGaps),
                }),
            ]),
            new SynthesisContext());

        Finding finding = findings.Should().ContainSingle().Subject;
        if (expectNote)
            finding.Narrative.Should().Contain($"{subKbGaps}% of free gaps are under 1 KB");
        else
            finding.Narrative.Should().NotContain("free gaps are under 1 KB");
    }

    [Fact]
    public async Task LockGraphRule_CountsContestedLocksWithRecursionAtLeastThree()
    {
        IReadOnlyList<Finding> findings = await new LockGraphSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("runtime.lock-pressure", "Lock Graph Analysis", new() { ["held-lock.count"] = Count(3) }),
                MakeContestedLock(recursionCount: 3),
                MakeContestedLock(recursionCount: 5),
                MakeContestedLock(recursionCount: 2),
            ]),
            new SynthesisContext());

        findings.Should().ContainSingle().Which.Narrative
            .Should().Contain("2 contested lock(s) show a recursion count ≥ 3");
    }

    [Fact]
    public async Task LockGraphRule_NoRecursionNote_WhenNoLockReachesThreshold()
    {
        IReadOnlyList<Finding> findings = await new LockGraphSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("runtime.lock-pressure", "Lock Graph Analysis", new() { ["held-lock.count"] = Count(1) }),
                MakeContestedLock(recursionCount: 2),
            ]),
            new SynthesisContext());

        findings.Should().ContainSingle().Which.Narrative.Should().NotContain("recursion count");
    }

    private static Observation MakeContestedLock(int recursionCount) => MakeObservation(
        "lock.contested",
        "Lock Graph Analysis",
        new() { ["waiting.count"] = Count(2), ["recursion.count"] = Count(recursionCount) });

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Absolute);

    private static Observation MakeObservation(string observationType, string analyzerKey, Dictionary<string, Measure> measures) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("section-notes-test"),
            AnalyzerKey = analyzerKey,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };
}
