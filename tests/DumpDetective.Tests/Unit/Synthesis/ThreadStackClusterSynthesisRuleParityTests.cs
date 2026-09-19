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
/// Field-for-field parity between <see cref="ThreadStackClusterFindingGenerator"/> (old) and
/// <see cref="ThreadStackClusterSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>ThreadStackClusterAnalyzer.EmitObservationsAsync</c>: one
/// whole-run <c>runtime.thread-cluster-diversity</c> observation plus one <c>thread.stack-cluster</c>
/// per <c>TopClusters</c> entry (already sorted descending by count — the rule trusts emission
/// order for the "dominant cluster"), carrying the signature/framework-pattern strings as
/// <c>Evidence</c> rather than <c>Measures</c>.
/// </remarks>
public sealed class ThreadStackClusterSynthesisRuleParityTests
{
    private readonly ThreadStackClusterFindingGenerator _oldGenerator = new();
    private readonly ThreadStackClusterSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task DominantClusterAboveThreshold_EmitsWarning() => AssertParity(new ThreadStackClusterDomainResult(
        1000, 5, 0, 0.5, ["MyApp.Worker.Run()"],
        TopClusters: [new ThreadClusterSnapshot(600, [], "MyApp.Worker.Run()")]));

    [Fact]
    public Task DominantClusterMatchesFrameworkPattern_DowngradesToInfo() => AssertParity(new ThreadStackClusterDomainResult(
        1000, 5, 0, 0.5, ["<No managed frames> (Threadpool)"],
        TopClusters: [new ThreadClusterSnapshot(600, [], "<No managed frames> (Threadpool)", FrameworkPattern: "Threadpool-idle")]));

    [Fact]
    public Task HighDiversity_SummaryIsInfo() => AssertParity(new ThreadStackClusterDomainResult(
        100, 80, 60, 80.0, ["Sig1", "Sig2"],
        TopClusters: [new ThreadClusterSnapshot(10, [], "Sig1")]));

    [Fact]
    public Task NoClusters_OnlyEmitsSummaryFinding() => AssertParity(new ThreadStackClusterDomainResult(
        0, 0, 0, 0, [], TopClusters: null));

    [Fact]
    public Task DominantClusterBelowFiftyPercent_NoWarningEvenWithoutFrameworkPattern() => AssertParity(new ThreadStackClusterDomainResult(
        1000, 50, 0, 40.0, ["MyApp.Worker.Run()"],
        TopClusters: [new ThreadClusterSnapshot(300, [], "MyApp.Worker.Run()")]));

    private async Task AssertParity(ThreadStackClusterDomainResult result)
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

    // ── Mirrors ThreadStackClusterAnalyzer.EmitObservationsAsync ─────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(ThreadStackClusterDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.RuntimeThreads };
        var observations = new List<Observation>();

        foreach (ThreadClusterSnapshot cluster in result.TopClusters ?? [])
        {
            var evidence = new List<EvidenceRef>
            {
                new() { Artifact = TestArtifact, Locator = "signature", Description = cluster.Signature },
            };
            if (cluster.FrameworkPattern is not null)
                evidence.Add(new EvidenceRef { Artifact = TestArtifact, Locator = "framework-pattern", Description = cluster.FrameworkPattern });

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "thread.stack-cluster",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["cluster.count"] = new Measure(cluster.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["threadpool-worker.count"] = new Measure(cluster.ThreadpoolWorkerCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gc.count"] = new Measure(cluster.GcCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["finalizer.count"] = new Measure(cluster.FinalizerCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Evidence = evidence,
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "runtime.thread-cluster-diversity",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["alive-thread.count"] = new Measure(result.AliveThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["unique-cluster.count"] = new Measure(result.UniqueClusters, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["diversity.percent"] = new Measure(result.DiversityPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
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
        AnalyzerKey = "Thread Stack Signature Clustering",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
