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
/// Field-for-field parity between <see cref="GCRootFindingGenerator"/> (old) and
/// <see cref="GCRootSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class GCRootSynthesisRuleParityTests
{
    private readonly GCRootFindingGenerator _oldGenerator = new();
    private readonly GCRootSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoSignals_ReturnsEmpty() => AssertParity(BuildResult());

    [Fact]
    public Task StaticRootBytesAtOrAboveOneHundredMb_Critical() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "StrongHandle", Count: 10, EstimatedRetainedBytes: 150_000_000UL, PctOfManagedHeap: 15.0),
    ]));

    [Fact]
    public Task StaticRootBytesAtOrAboveTenMb_BelowCritical_Warning() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "StrongHandle", Count: 5, EstimatedRetainedBytes: 20_000_000UL, PctOfManagedHeap: 2.0),
    ]));

    [Fact]
    public Task StaticRootBytesBelowTenMb_NoFinding() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "StrongHandle", Count: 1, EstimatedRetainedBytes: 1_000_000UL, PctOfManagedHeap: 0.1),
    ]));

    [Fact]
    public Task StaticRootWithTopSuspectPopulated() => AssertParity(BuildResult(
        byKind: [new RootKindSummary(Kind: "StrongHandle", Count: 10, EstimatedRetainedBytes: 150_000_000UL, PctOfManagedHeap: 15.0)],
        topRootsBySeverity: [Root("MyApp.Cache", 0x1000, 150_000_000UL)],
        totalRoots: 42));

    [Fact]
    public Task FinalizerCountAtOrAboveFiveThousand_Warning() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "FinalizerQueue", Count: 6_000, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task FinalizerCountAtOrAboveFiveHundred_BelowFiveThousand_Info() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "FinalizerQueue", Count: 800, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task FinalizerCountBelowFiveHundred_NoFinding() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "FinalizerQueue", Count: 100, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task PinnedHandlesAtOrAboveFiveHundred_Warning() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "PinnedHandle", Count: 600, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task PinnedHandlesAtOrAboveOneHundred_BelowFiveHundred_Info() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "AsyncPinnedHandle", Count: 150, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task PinnedAndAsyncPinnedCombine() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "PinnedHandle", Count: 300, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
        new RootKindSummary(Kind: "AsyncPinnedHandle", Count: 300, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task PinnedHandlesBelowOneHundred_NoFinding() => AssertParity(BuildResult(byKind:
    [
        new RootKindSummary(Kind: "PinnedHandle", Count: 10, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
    ]));

    [Fact]
    public Task SubgraphWalkCapped_Info() => AssertParity(BuildResult(subgraphWalkCapped: true, subgraphWalkCappedCount: 3));

    [Fact]
    public Task SubgraphWalkNotCapped_NoFinding() => AssertParity(BuildResult(subgraphWalkCapped: false, subgraphWalkCappedCount: 0));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        byKind:
        [
            new RootKindSummary(Kind: "StrongHandle", Count: 10, EstimatedRetainedBytes: 150_000_000UL, PctOfManagedHeap: 15.0),
            new RootKindSummary(Kind: "FinalizerQueue", Count: 6_000, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
            new RootKindSummary(Kind: "PinnedHandle", Count: 300, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
            new RootKindSummary(Kind: "AsyncPinnedHandle", Count: 300, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0),
        ],
        topRootsBySeverity: [Root("MyApp.Cache", 0x1000, 150_000_000UL)],
        totalRoots: 12_500,
        subgraphWalkCapped: true,
        subgraphWalkCappedCount: 3));

    private static RootFinding Root(string typeName, ulong targetAddress, ulong estimatedRetainedBytes) => new(
        RootKind: "StrongHandle",
        RootAddress: 0x2000,
        FieldDescription: null,
        TargetTypeName: typeName,
        TargetAddress: targetAddress,
        EstimatedRetainedBytes: estimatedRetainedBytes);

    private async Task AssertParity(GCRootDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(GCRootDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapRoots, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.HeapTypes,
        };

        var observations = new List<Observation>();

        // Emitted in the given list order, deliberately never re-sorted here — matches the real
        // analyzer, whose TopRootsBySeverity is already severity-sorted before EmitObservationsAsync's
        // emission loop runs over it.
        foreach (RootFinding finding in result.TopRootsBySeverity)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.root",
                Subjects = [new TypeRef { CanonicalName = finding.TargetTypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(finding.EstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = finding.RetainedBytesIsExact ? 1.0 : 0.7,
            });
        }

        foreach (RootKindSummary ks in result.ByKind)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "root.kind-summary",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["kind.ordinal"] = new Measure((double)(int)Enum.Parse<DumpDetective.Sdk.Analysis.HeapRootKind>(ks.Kind), MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["count"] = new Measure(ks.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["retained.bytes"] = new Measure(ks.EstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "root.population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(result.TotalRoots, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["subgraph-walk-capped"] = new Measure(result.SubgraphWalkCapped ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["subgraph-walk-capped.count"] = new Measure(result.SubgraphWalkCappedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "GC Root Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static GCRootDomainResult BuildResult(
        IReadOnlyList<RootKindSummary>? byKind = null,
        IReadOnlyList<RootFinding>? topRootsBySeverity = null,
        int totalRoots = 100,
        bool subgraphWalkCapped = false,
        int subgraphWalkCappedCount = 0) => new(
        TotalRoots: totalRoots,
        ByKind: byKind ?? [],
        TopRootsBySeverity: topRootsBySeverity ?? [],
        RootOwnedSubgraphs: [],
        SubgraphWalkCapped: subgraphWalkCapped,
        SubgraphWalkCappedCount: subgraphWalkCappedCount);
}
