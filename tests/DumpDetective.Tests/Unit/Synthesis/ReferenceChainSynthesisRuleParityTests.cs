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
/// Field-for-field parity between <see cref="ReferenceChainFindingGenerator"/> (old) and
/// <see cref="ReferenceChainSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class ReferenceChainSynthesisRuleParityTests
{
    private readonly ReferenceChainFindingGenerator _oldGenerator = new();
    private readonly ReferenceChainSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoAnalyzedSamples_EmitsOnlyNoSampleFinding() => AssertParity(
        BuildResult(analyzedSamples: 0, retainedSamples: 0, retainedPercent: 0));

    [Fact]
    public Task RetentionBelowThreshold_EmitsInfoRetentionCoverage() => AssertParity(
        BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0));

    [Fact]
    public Task RetentionAboveThreshold_EmitsWarningRetentionCoverage() => AssertParity(
        BuildResult(analyzedSamples: 10, retainedSamples: 8, retainedPercent: 80.0));

    [Fact]
    public Task NoTraversalLimitedSamples_EmitsNoTraversalLimitFinding() => AssertParity(
        BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0, traversalLimitedSamples: 0));

    [Fact]
    public Task TraversalLimitedSamplesBelowWarningThreshold_EmitsInfoFinding() => AssertParity(
        BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0, traversalLimitedSamples: 1));

    [Fact]
    public Task TraversalLimitedSamplesAtWarningThreshold_EmitsWarningFinding() => AssertParity(
        BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0, traversalLimitedSamples: 2));

    [Fact]
    public Task NoTypesExclusivelyFinalizerRetained_EmitsNoFinalizerOnlyFinding() => AssertParity(BuildResult(
        analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0,
        traces: [BuildTrace("App.Widget", count: 500, retainedSampleCount: 3, sampleCount: 3,
            dominantSampleRootKind: "StaticVar", dominantSampleRootKindCount: 3)]));

    [Fact]
    public Task TypePartiallyFinalizerRetained_EmitsNoFinalizerOnlyFinding() => AssertParity(BuildResult(
        analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0,
        traces: [BuildTrace("App.Handle", count: 500, retainedSampleCount: 5, sampleCount: 5,
            dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 4)]));

    [Fact]
    public Task TypeExclusivelyFinalizerRetainedBelowPopulationThreshold_EmitsInfoFinding() => AssertParity(BuildResult(
        analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0,
        traces: [BuildTrace("App.SmallHandle", count: 40, retainedSampleCount: 3, sampleCount: 3,
            dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 3)]));

    [Fact]
    public Task TypeExclusivelyFinalizerRetainedAtScale_EmitsWarningFinding() => AssertParity(BuildResult(
        analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0,
        traces: [BuildTrace("App.LeakyHandle", count: 5_000, retainedSampleCount: 3, sampleCount: 3,
            dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 3)]));

    [Fact]
    public Task MultipleTypesExclusivelyFinalizerRetained_ListsAllInEvidence() => AssertParity(BuildResult(
        analyzedSamples: 2, retainedSamples: 2, retainedPercent: 100.0,
        traces:
        [
            BuildTrace("App.HandleA", count: 40, retainedSampleCount: 2, sampleCount: 2,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 2),
            BuildTrace("App.HandleB", count: 5_000, retainedSampleCount: 4, sampleCount: 4,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 4),
        ]));

    [Fact]
    public Task NoSharedRootGroups_EmitsNoSharedRootFinding() => AssertParity(
        BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0));

    [Fact]
    public Task SharedRootGroupBelowTypeCountThreshold_EmitsInfoFinding() => AssertParity(BuildResult(
        analyzedSamples: 2, retainedSamples: 2, retainedPercent: 100.0,
        sharedRootGroups: [new ReferenceChainSharedRootGroup(0x2000, "StaticVar", ["App.CacheA", "App.CacheB"])]));

    [Fact]
    public Task SharedRootGroupAtTypeCountThreshold_EmitsWarningFinding() => AssertParity(BuildResult(
        analyzedSamples: 3, retainedSamples: 3, retainedPercent: 100.0,
        sharedRootGroups: [new ReferenceChainSharedRootGroup(0x3000, "StaticVar", ["App.CacheA", "App.CacheB", "App.CacheC"])]));

    [Fact]
    public Task MultipleSharedRootGroups_ListsAllInEvidence() => AssertParity(BuildResult(
        analyzedSamples: 5, retainedSamples: 5, retainedPercent: 100.0,
        sharedRootGroups:
        [
            new ReferenceChainSharedRootGroup(0x3000, "StaticVar", ["App.CacheA", "App.CacheB", "App.CacheC"]),
            new ReferenceChainSharedRootGroup(0x4000, "Stack", ["App.HandleD", "App.HandleE"]),
        ]));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        analyzedSamples: 10, retainedSamples: 8, retainedPercent: 80.0, traversalLimitedSamples: 3,
        traces:
        [
            BuildTrace("App.LeakyHandle", count: 5_000, retainedSampleCount: 3, sampleCount: 3,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 3),
        ],
        sharedRootGroups: [new ReferenceChainSharedRootGroup(0x3000, "StaticVar", ["App.CacheA", "App.CacheB", "App.CacheC"])]));

    private static ReferenceTypeSampleSnapshot BuildTrace(
        string typeName,
        int count,
        int retainedSampleCount,
        int sampleCount,
        string? dominantSampleRootKind,
        int dominantSampleRootKindCount)
    {
        return new ReferenceTypeSampleSnapshot(
            TypeName: typeName,
            Count: count,
            TotalSizeBytes: (ulong)count * 32,
            SampleAddress: 0x1000,
            SampleObjectType: typeName,
            SampleObjectSize: 32,
            HasGcRoot: retainedSampleCount > 0,
            RootKind: dominantSampleRootKind,
            RootPath: null,
            PathHops: null,
            TraversalLimited: false,
            SampleCount: sampleCount,
            RetainedSampleCount: retainedSampleCount,
            DominantSampleRootKind: dominantSampleRootKind,
            DominantSampleRootKindCount: dominantSampleRootKindCount);
    }

    private static ReferenceChainDomainResult BuildResult(
        int analyzedSamples,
        int retainedSamples,
        double retainedPercent,
        int traversalLimitedSamples = 0,
        IReadOnlyList<ReferenceTypeSampleSnapshot>? traces = null,
        IReadOnlyList<ReferenceChainSharedRootGroup>? sharedRootGroups = null)
    {
        return new ReferenceChainDomainResult(
            AnalyzedSamples: analyzedSamples,
            RetainedSamples: retainedSamples,
            RetainedPercent: retainedPercent,
            TopTypeSampleTraces: traces,
            TraversalLimitedSamples: traversalLimitedSamples,
            SharedRootGroups: sharedRootGroups);
    }

    private async Task AssertParity(ReferenceChainDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(ReferenceChainDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapTypes, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapReferences, CapabilityVocabulary.HeapRoots,
        };
        var observations = new List<Observation>();

        foreach (ReferenceTypeSampleSnapshot trace in result.TopTypeSampleTraces ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "reference-chain.type-sample",
                Subjects = [new TypeRef { CanonicalName = trace.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(trace.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["sample.count"] = new Measure(trace.SampleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["retained-sample.count"] = new Measure(trace.RetainedSampleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dominant-root-kind.count"] = new Measure(trace.DominantSampleRootKindCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence = trace.DominantSampleRootKind is not null
                    ? [new EvidenceRef { Artifact = TestArtifact, Locator = "dominant-root-kind", Description = trace.DominantSampleRootKind }]
                    : [],
            });
        }

        foreach (ReferenceChainSharedRootGroup group in result.SharedRootGroups ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "reference-chain.shared-root-group",
                Subjects = [new ObjectRef { Address = group.RootAddress, Artifact = TestArtifact, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["type.count"] = new Measure(group.TypeNames.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence =
                [
                    new EvidenceRef { Artifact = TestArtifact, Locator = "root-kind", Description = group.RootKind },
                    new EvidenceRef { Artifact = TestArtifact, Locator = "type-names", Description = string.Join(", ", group.TypeNames) },
                ],
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "reference-chain.population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["analyzed.count"] = new Measure(result.AnalyzedSamples, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["retained.count"] = new Measure(result.RetainedSamples, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["retained.percent"] = new Measure(result.RetainedPercent, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                ["traversal-limited.count"] = new Measure(result.TraversalLimitedSamples, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Reference Chain Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
