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
/// Field-for-field parity between <see cref="LockGraphFindingGenerator"/> (old) and
/// <see cref="LockGraphSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>LockGraphAnalyzer.Analyze</c>'s own emission: one
/// <c>lock.contested</c> per contested lock, one <c>thread.blocked</c> per deadlock candidate
/// (both unbounded, one-per-entry — that's why the rule reconstructs
/// <c>ContestedLockCount</c>/<c>DeadlockCandidateCount</c> by counting them instead of reading a
/// dedicated measure), plus the one whole-run <c>runtime.lock-pressure</c> observation for
/// <c>TotalHeldLocks</c>, which has no other observation to stand in for it.
/// </remarks>
public sealed class LockGraphSynthesisRuleParityTests
{
    private readonly LockGraphFindingGenerator _oldGenerator = new();
    private readonly LockGraphSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task ContestedLocksPresent_EmitsWarningFinding() => AssertParity(BuildResult(contestedLockCount: 3));

    [Fact]
    public Task NoContestedLocks_EmitsInfoFinding() => AssertParity(BuildResult(contestedLockCount: 0));

    [Fact]
    public Task TwoOrMoreDeadlockCandidates_RecommendsCycleDetection() => AssertParity(BuildResult(
        contestedLockCount: 2,
        deadlockCandidateDetails:
        [
            new DeadlockCandidateSnapshot(ManagedThreadId: 1, OsThreadId: 101, LockObjectTypes: ["System.Object"], LockObjectAddresses: [0x1000], BlockedAtFrame: "Foo.Bar()"),
            new DeadlockCandidateSnapshot(ManagedThreadId: 2, OsThreadId: 102, LockObjectTypes: ["System.Object"], LockObjectAddresses: [0x2000], BlockedAtFrame: "Baz.Qux()"),
        ]));

    [Fact]
    public Task ContestedNoDeadlockCandidates_RecommendsReducingLockScope() => AssertParity(BuildResult(contestedLockCount: 5));

    [Fact]
    public Task NoContentionAtAll_RecommendsNoAction() => AssertParity(BuildResult(contestedLockCount: 0));

    [Fact]
    public Task SingleDeadlockCandidate_StillWarningNotCritical() => AssertParity(BuildResult(
        contestedLockCount: 1,
        deadlockCandidateDetails:
        [
            new DeadlockCandidateSnapshot(ManagedThreadId: 7, OsThreadId: 707, LockObjectTypes: ["System.Object"], LockObjectAddresses: [0x3000], BlockedAtFrame: "Quux.Corge()"),
        ]));

    private static LockGraphDomainResult BuildResult(
        int contestedLockCount,
        IReadOnlyList<DeadlockCandidateSnapshot>? deadlockCandidateDetails = null) => new(
        TotalHeldLocks: 20,
        ContestedLockCount: contestedLockCount,
        MaxWaitersOnSingleLock: contestedLockCount > 0 ? 2 : 0,
        DeadlockCandidateCount: deadlockCandidateDetails?.Count ?? 0,
        DeadlockCandidateDetails: deadlockCandidateDetails);

    private async Task AssertParity(LockGraphDomainResult result)
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

    // ── Mirrors LockGraphAnalyzer.Analyze's own emission ─────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(LockGraphDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.RuntimeLocks, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.RuntimeThreads,
        };
        var observations = new List<Observation>();

        for (int i = 0; i < result.ContestedLockCount; i++)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "lock.contested",
                Subjects = [new TypeRef { CanonicalName = "System.Object", Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["waiting.count"] = new Measure(1, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["recursion.count"] = new Measure(0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (DeadlockCandidateSnapshot dc in result.DeadlockCandidateDetails ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "thread.blocked",
                Subjects = [new ThreadRef { OsThreadId = dc.OsThreadId, ManagedThreadId = (int)dc.ManagedThreadId, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["locks-held.count"] = new Measure(dc.LockObjectTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "runtime.lock-pressure",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["held-lock.count"] = new Measure(result.TotalHeldLocks, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "Lock Graph Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
