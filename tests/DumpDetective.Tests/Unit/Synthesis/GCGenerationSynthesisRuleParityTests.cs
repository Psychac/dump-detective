using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="GCGenerationFindingGenerator"/> (old) and
/// <see cref="GCGenerationSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>GCGenerationAnalyzer.EmitObservationAsync</c>'s own
/// selection (<c>SelectTopTypes</c>/<c>IsFrameworkType</c>), not the old finding generator's —
/// the point of these fixtures is to prove the rule reconstructs the old generator's text from
/// what the *analyzer* now decides to emit, the same real production coupling
/// <c>AllocationPatternSynthesisRuleParityTests</c> established for its own rule.
/// </remarks>
public sealed class GCGenerationSynthesisRuleParityTests
{
    private readonly GCGenerationFindingGenerator _oldGenerator = new();
    private readonly GCGenerationSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task Gen2PctAboveSeventyFive_EmitsCriticalFinding() => AssertParity(BuildResult(gen2Pct: 80.0));

    [Fact]
    public Task Gen2PctBetweenFiftyAndSeventyFive_EmitsWarningFinding() => AssertParity(BuildResult(gen2Pct: 60.0));

    [Fact]
    public Task Gen2PctBelowFifty_EmitsNoGen2Finding() => AssertParity(BuildResult(gen2Pct: 40.0));

    [Fact]
    public Task Gen0PctAtOrAboveThreshold_EmitsWarningFinding() => AssertParity(
        BuildResult(totalObjects: 1000, gen0Objects: 500, gen0PressureThresholdPercent: 40.0));

    [Fact]
    public Task LohPctAtThreshold_BelowThirtyFive_EmitsInfoFinding() => AssertParity(
        BuildResult(lohPercent: 25.0, lohThresholdPercent: 20.0));

    [Fact]
    public Task LohPctAtOrAboveThirtyFive_EmitsWarningFinding() => AssertParity(
        BuildResult(lohPercent: 40.0, lohThresholdPercent: 20.0));

    [Fact]
    public Task PohPctAtThreshold_BelowTwenty_EmitsInfoFinding() => AssertParity(
        BuildResult(totalObjects: 1000, pohObjects: 100, pohBytes: 1024, pohThresholdPercent: 5.0));

    [Fact]
    public Task PohPctAtOrAboveTwenty_EmitsWarningFinding() => AssertParity(
        BuildResult(totalObjects: 1000, pohObjects: 250, pohBytes: 1024, pohThresholdPercent: 5.0));

    [Fact]
    public Task PohBytesZero_SuppressesPohFindingEvenAboveThreshold() => AssertParity(
        BuildResult(totalObjects: 1000, pohObjects: 250, pohBytes: 0, pohThresholdPercent: 5.0));

    [Fact]
    public Task Gen2Finding_IncludesFinalizableCrossReference_WhenFinalizableGen2ObjectsPresent() => AssertParity(
        BuildGen2PressureResult(finalizableGen2Count: 150, finalizableGen2Bytes: 45_000));

    [Fact]
    public Task Gen2Finding_OmitsFinalizableCrossReference_WhenNoFinalizableGen2Objects() => AssertParity(
        BuildGen2PressureResult(finalizableGen2Count: 0, finalizableGen2Bytes: 0));

    [Fact]
    public Task Gen2Finding_FallbackMode_IncludesFallbackWarning() => AssertParity(
        BuildGen2PressureResult(finalizableGen2Count: 0, finalizableGen2Bytes: 0, fallbackMode: true, genBytesAreApproximate: true));

    [Fact]
    public Task Gen2Finding_ApproximateBytes_IncludesApproximationWarning() => AssertParity(
        BuildGen2PressureResult(finalizableGen2Count: 0, finalizableGen2Bytes: 0, genBytesAreApproximate: true));

    [Fact]
    public Task Gen0Finding_WithTopAllocatingTypes_IncludesThemInEvidence() => AssertParity(BuildResult(
        totalObjects: 1000, gen0Objects: 500, gen0PressureThresholdPercent: 40.0,
        profiles:
        [
            new TypeGenerationProfile("MyApp.Widget", Gen0Count: 300, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
            new TypeGenerationProfile("System.String", Gen0Count: 200, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
        ]));

    [Fact]
    public Task Gen0Finding_PrefersAppNamespaceTypesOverFramework() => AssertParity(BuildResult(
        totalObjects: 1000, gen0Objects: 500, gen0PressureThresholdPercent: 40.0,
        profiles:
        [
            new TypeGenerationProfile("System.String", Gen0Count: 500, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
            new TypeGenerationProfile("System.Object", Gen0Count: 400, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
            new TypeGenerationProfile("MyApp.Widget", Gen0Count: 10, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
        ]));

    [Fact]
    public Task Gen2Finding_WithTopAccumulatingTypesByBytes_IncludesThemInEvidence() => AssertParity(BuildGen2PressureResult(
        finalizableGen2Count: 0, finalizableGen2Bytes: 0,
        profiles:
        [
            new TypeGenerationProfile("MyApp.Cache", Gen0Count: 0, Gen1Count: 0, Gen2Count: 100, LohCount: 0, TotalBytes: 500_000, Gen2Bytes: 500_000),
            new TypeGenerationProfile("System.String", Gen0Count: 0, Gen1Count: 0, Gen2Count: 50, LohCount: 0, TotalBytes: 100_000, Gen2Bytes: 100_000),
        ]));

    [Fact]
    public Task Gen2Finding_WithTopAccumulatingTypesByCount_WhenNoGen2Bytes() => AssertParity(BuildGen2PressureResult(
        finalizableGen2Count: 0, finalizableGen2Bytes: 0,
        profiles:
        [
            new TypeGenerationProfile("MyApp.Cache", Gen0Count: 0, Gen1Count: 0, Gen2Count: 500, LohCount: 0),
            new TypeGenerationProfile("System.String", Gen0Count: 0, Gen1Count: 0, Gen2Count: 100, LohCount: 0),
        ]));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(new GCGenerationDomainResult(
        Gen0Bytes: 300_000_000, Gen0Objects: 500,
        Gen1Bytes: 10_000, Gen1Objects: 10,
        Gen2Bytes: 800_000, Gen2Objects: 800,
        LohBytes: 400_000, LohPercent: 40.0,
        TotalObjects: 1000, LohObjects: 5,
        TopLohTypes: [],
        PohBytes: 1024, PohObjects: 250,
        Gen2Pct: 80.0,
        PerTypeGenerationProfiles:
        [
            new TypeGenerationProfile("MyApp.Widget", Gen0Count: 300, Gen1Count: 0, Gen2Count: 0, LohCount: 0),
            new TypeGenerationProfile("MyApp.Cache", Gen0Count: 0, Gen1Count: 0, Gen2Count: 800, LohCount: 0, TotalBytes: 800_000, Gen2Bytes: 800_000),
        ],
        LohThresholdPercent: 20.0,
        Gen0PressureThresholdPercent: 40.0,
        PohThresholdPercent: 5.0,
        FinalizableGen2Count: 150,
        FinalizableGen2Bytes: 45_000));

    private static GCGenerationDomainResult BuildResult(
        int totalObjects = 0,
        long gen0Objects = 0,
        long pohObjects = 0,
        ulong pohBytes = 0,
        double gen2Pct = 0.0,
        double lohPercent = 0.0,
        double lohThresholdPercent = 20.0,
        double gen0PressureThresholdPercent = 40.0,
        double pohThresholdPercent = 5.0,
        IReadOnlyList<TypeGenerationProfile>? profiles = null) => new(
        Gen0Bytes: 0, Gen0Objects: gen0Objects,
        Gen1Bytes: 0, Gen1Objects: 0,
        Gen2Bytes: 0, Gen2Objects: 0,
        LohBytes: 0, LohPercent: lohPercent,
        TotalObjects: totalObjects, LohObjects: 0,
        TopLohTypes: [],
        PohBytes: pohBytes, PohObjects: pohObjects,
        Gen2Pct: gen2Pct,
        PerTypeGenerationProfiles: profiles ?? [],
        LohThresholdPercent: lohThresholdPercent,
        Gen0PressureThresholdPercent: gen0PressureThresholdPercent,
        PohThresholdPercent: pohThresholdPercent);

    private static GCGenerationDomainResult BuildGen2PressureResult(
        long finalizableGen2Count, ulong finalizableGen2Bytes,
        bool fallbackMode = false, bool genBytesAreApproximate = false,
        IReadOnlyList<TypeGenerationProfile>? profiles = null) => new(
        Gen0Bytes: 10_000, Gen0Objects: 10,
        Gen1Bytes: 10_000, Gen1Objects: 10,
        Gen2Bytes: 800_000, Gen2Objects: 800,
        LohBytes: 0, LohPercent: 0,
        TotalObjects: 820, LohObjects: 0,
        TopLohTypes: [],
        Gen2Pct: 97.6,
        PerTypeGenerationProfiles: profiles ?? [],
        GenBytesAreApproximate: genBytesAreApproximate,
        FallbackMode: fallbackMode,
        FinalizableGen2Count: finalizableGen2Count,
        FinalizableGen2Bytes: finalizableGen2Bytes);

    private async Task AssertParity(GCGenerationDomainResult result)
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

    // ── Mirrors GCGenerationAnalyzer.EmitObservationAsync/SelectTopTypes/IsFrameworkType ─────────

    private static IReadOnlyList<Observation> ToObservations(GCGenerationDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
        IReadOnlyList<TypeGenerationProfile> profiles = result.PerTypeGenerationProfiles ?? [];

        bool anyGen2Bytes = false;
        for (int i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].Gen2Bytes > 0)
            {
                anyGen2Bytes = true;
                break;
            }
        }

        ulong totalManagedBytes = result.Gen0Bytes + result.Gen1Bytes + result.Gen2Bytes + result.LohBytes + result.PohBytes;
        double gen2Fraction = totalManagedBytes == 0 ? 0.0 : result.Gen2Bytes / (double)totalManagedBytes;
        double lohFraction = totalManagedBytes == 0 ? 0.0 : result.LohBytes / (double)totalManagedBytes;

        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.generation-composition",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["gen0.bytes"] = new Measure(result.Gen0Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen1.bytes"] = new Measure(result.Gen1Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.bytes"] = new Measure(result.Gen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(result.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["poh.bytes"] = new Measure(result.PohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.fraction"] = new Measure(gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["loh.fraction"] = new Measure(lohFraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen0.count"] = new Measure(result.Gen0Objects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(result.Gen2Objects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["poh.count"] = new Measure(result.PohObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total.count"] = new Measure(result.TotalObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.percent"] = new Measure(result.LohPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["gen2.percent"] = new Measure(result.Gen2Pct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["loh-pressure.threshold"] = new Measure(result.LohThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["gen0-pressure.threshold"] = new Measure(result.Gen0PressureThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["poh-pressure.threshold"] = new Measure(result.PohThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["has-gen2-bytes"] = new Measure(anyGen2Bytes ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["finalizable-gen2.count"] = new Measure(result.FinalizableGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["finalizable-gen2.bytes"] = new Measure(result.FinalizableGen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = result.GenBytesAreApproximate ? 0.5 : 1.0,
            },
        };

        // Distinguish Fidelity from Confidence, matching the analyzer's own emission exactly.
        Observation composition = observations[0];
        observations[0] = composition with
        {
            Provenance = composition.Provenance with { Fidelity = result.FallbackMode ? FidelityLevel.Degraded : FidelityLevel.Full },
        };

        foreach (TypeGenerationProfile type in SelectTopTypes(profiles, p => p.Gen0Count, p => p.Gen0Count))
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.generation-top-allocator",
                Subjects = [new TypeRef { CanonicalName = type.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["gen0.count"] = new Measure(type.Gen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        IReadOnlyList<TypeGenerationProfile> topAccumulators = anyGen2Bytes
            ? SelectTopTypes(profiles, p => (long)Math.Min(p.Gen2Bytes, long.MaxValue), p => (long)Math.Min(p.Gen2Bytes, long.MaxValue))
            : SelectTopTypes(profiles, p => p.Gen2Count, p => p.Gen2Count);

        foreach (TypeGenerationProfile type in topAccumulators)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.generation-top-accumulator",
                Subjects = [new TypeRef { CanonicalName = type.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["gen2.bytes"] = new Measure(type.Gen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(type.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static List<TypeGenerationProfile> SelectTopTypes(
        IReadOnlyList<TypeGenerationProfile> profiles,
        Func<TypeGenerationProfile, long> hasSignal,
        Func<TypeGenerationProfile, long> rankKey,
        int maxCount = 3)
    {
        var candidates = new List<TypeGenerationProfile>(profiles.Count);
        for (int i = 0; i < profiles.Count; i++)
            if (hasSignal(profiles[i]) > 0)
                candidates.Add(profiles[i]);

        candidates.Sort((a, b) => rankKey(b).CompareTo(rankKey(a)));

        var selected = new List<TypeGenerationProfile>(maxCount);
        for (int i = 0; i < candidates.Count && selected.Count < maxCount; i++)
            if (!IsFrameworkType(candidates[i].TypeName))
                selected.Add(candidates[i]);
        for (int i = 0; i < candidates.Count && selected.Count < maxCount; i++)
            if (IsFrameworkType(candidates[i].TypeName))
                selected.Add(candidates[i]);

        return selected;
    }

    private static bool IsFrameworkType(string typeName) =>
        typeName.StartsWith("System.", StringComparison.Ordinal) ||
        typeName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        typeName.StartsWith("Windows.", StringComparison.Ordinal) ||
        typeName.StartsWith("Mono.", StringComparison.Ordinal) ||
        typeName is "System" or "Microsoft" or "Windows";

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "GC Generation Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
