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

    [Theory]
    [InlineData(3_000UL, "≫", "holds a large external graph", "3.00x")]
    [InlineData(1_200UL, ">", "holds some external references", "1.20x")]
    [InlineData(1_000UL, "≈", "largely self-contained", "1.00x")]
    public async Task DominatorRule_DescribesTopLongLivedTypeRetentionShape(ulong retainedBytes, string relation, string shape, string ratio)
    {
        IReadOnlyList<Finding> findings = await new DominatorSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeRetention("App.Gen0Heavy", gen2: 0, lohBytes: 0, shallow: 1_000, retained: 50_000),
                MakeRetention("App.LongLivedA", gen2: 10, lohBytes: 0, shallow: 1_000, retained: 1_000),
                MakeRetention("App.LongLivedB", gen2: 10, lohBytes: 500, shallow: 1_000, retained: retainedBytes),
            ]),
            new SynthesisContext());

        findings.Single(f => f.Fingerprint.EndsWith("top-dominator-type", StringComparison.Ordinal)).Narrative
            .Should().EndWith($"Top long-lived (Gen2/LOH) type App.LongLivedB: retained {relation} shallow ({ratio}) — {shape}.");
    }

    [Fact]
    public async Task DominatorRule_NoRetentionShapeSentence_WithoutAnyGen2OrLohType()
    {
        IReadOnlyList<Finding> findings = await new DominatorSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([MakeRetention("App.Gen0Heavy", gen2: 0, lohBytes: 0, shallow: 1_000, retained: 50_000)]),
            new SynthesisContext());

        findings.Single(f => f.Fingerprint.EndsWith("top-dominator-type", StringComparison.Ordinal)).Narrative
            .Should().NotContain("Top long-lived");
    }

    private static Observation MakeRetention(string typeName, long gen2, ulong lohBytes, ulong shallow, ulong retained) =>
        MakeObservation("type.retention", "Dominator Analysis", new()
        {
            ["instance.count"] = Count(10),
            ["shallow.bytes"] = new Measure(shallow, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["estimated-retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["gen2.count"] = Count(gen2),
            ["loh.bytes"] = new Measure(lohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
        }, [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    private static Observation MakeContestedLock(int recursionCount) => MakeObservation(
        "lock.contested",
        "Lock Graph Analysis",
        new() { ["waiting.count"] = Count(2), ["recursion.count"] = Count(recursionCount) });

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Absolute);

    private static Observation MakeObservation(
        string observationType, string analyzerKey, Dictionary<string, Measure> measures, IReadOnlyList<EntityRef>? subjects = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjects ?? [],
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
