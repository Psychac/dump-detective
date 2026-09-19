using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="StringFindingGenerator"/> (old) and
/// <see cref="StringSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>StringAnalyzer.Analyze</c>'s whole-run <c>type.string-composition</c>
/// emission — <c>very-long-string.count</c>/<c>.bytes</c> are computed from the (already-truncated)
/// <c>VeryLongStrings</c> list, the same exact-post-truncation total the analyzer itself emits.
/// </remarks>
public sealed class StringSynthesisRuleParityTests
{
    private readonly StringFindingGenerator _oldGenerator = new();
    private readonly StringSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task DuplicationRatioAboveHalf_WithPatterns_EmitsCriticalFinding() => AssertParity(
        BuildResult(duplicationRatio: 0.7, duplicatePatternCount: 50));

    [Fact]
    public Task DuplicatePatternsPresent_RatioAtOrBelowHalf_EmitsWarningFinding() => AssertParity(
        BuildResult(duplicationRatio: 0.3, duplicatePatternCount: 10));

    [Fact]
    public Task NoDuplicatePatterns_EmitsNoDuplicationFinding() => AssertParity(
        BuildResult(duplicationRatio: 0.0, duplicatePatternCount: 0));

    [Fact]
    public Task LohStringBytesPresent_EmitsWarningFinding() => AssertParity(
        BuildResult(lohStringBytes: 5_000_000UL));

    [Fact]
    public Task VeryLongStringsPresent_EmitsInfoFinding() => AssertParity(
        BuildResult(veryLongStrings: [new LongStringEntry(Address: 0x2000, CharLength: 200_000, SizeBytes: 400_000UL)]));

    [Fact]
    public Task PctOfManagedHeapAboveTwenty_EmitsWarningFinding() => AssertParity(
        BuildResult(pctOfManagedHeap: 35.0));

    [Fact]
    public Task LowSamplingCoverage_EmitsInfoFinding() => AssertParity(
        BuildResult(samplingCoverage: 0.02));

    [Fact]
    public Task ZeroSamplingCoverage_EmitsNoCoverageFinding() => AssertParity(
        BuildResult(samplingCoverage: 0.0));

    [Fact]
    public Task AllBelowThresholds_EmitsNoFindings() => AssertParity(BuildResult());

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        duplicationRatio: 0.7,
        duplicatePatternCount: 50,
        lohStringBytes: 5_000_000UL,
        veryLongStrings: [new LongStringEntry(Address: 0x2000, CharLength: 200_000, SizeBytes: 400_000UL)],
        pctOfManagedHeap: 35.0,
        samplingCoverage: 0.02));

    private static StringDomainResult BuildResult(
        double duplicationRatio = 0.0,
        int duplicatePatternCount = 0,
        ulong lohStringBytes = 0,
        IReadOnlyList<LongStringEntry>? veryLongStrings = null,
        double pctOfManagedHeap = 0.0,
        double samplingCoverage = 0.0) => new(
        TotalStrings: 100_000,
        TotalStringMemoryBytes: 10_000_000,
        SampledUniquePatterns: 90_000,
        DuplicatePatternCount: duplicatePatternCount,
        DuplicateWastedBytes: 1_000_000,
        DuplicationRatio: duplicationRatio,
        PctOfManagedHeap: pctOfManagedHeap,
        TopDuplicates: [],
        VeryLongStrings: veryLongStrings ?? [],
        LohStringBytes: lohStringBytes,
        InternedStringCount: 0,
        InternedStringBytes: 0,
        Gen0StringCount: 0,
        Gen1StringCount: 0,
        Gen2StringCount: 0,
        Gen2StringBytes: 0,
        StringsSampled: 100_000,
        SamplingCoverage: samplingCoverage);

    private async Task AssertParity(StringDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    // ── Mirrors StringAnalyzer.Analyze's own emission ────────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(StringDomainResult result)
    {
        ulong veryLongStringBytes = 0;
        foreach (LongStringEntry entry in result.VeryLongStrings)
            veryLongStringBytes += entry.SizeBytes;

        return
        [
            new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.string-composition",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["duplication.ratio"] = new Measure(result.DuplicationRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["duplicate-pattern.count"] = new Measure(result.DuplicatePatternCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["duplicate-wasted.bytes"] = new Measure(result.DuplicateWastedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["loh-string.bytes"] = new Measure(result.LohStringBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["very-long-string.count"] = new Measure(result.VeryLongStrings.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["very-long-string.bytes"] = new Measure(veryLongStringBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pct-of-managed-heap.percent"] = new Measure(result.PctOfManagedHeap, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["total-string-memory.bytes"] = new Measure(result.TotalStringMemoryBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["sampling-coverage.ratio"] = new Measure(result.SamplingCoverage, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["strings-sampled.count"] = new Measure(result.StringsSampled, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "String Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            },
        ];
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };
}
