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
/// Field-for-field parity between <see cref="CrashFindingGenerator"/> (old) and
/// <see cref="CrashSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// The old generator's weighted-average <c>ConfidenceScore</c> and tier-distribution
/// <c>Caveats</c> are deliberately not reproduced, same choice every prior rule in this playbook
/// made for evidence-based confidence overrides — see the rule's own remarks.
/// </remarks>
public sealed class CrashSynthesisRuleParityTests
{
    private readonly CrashFindingGenerator _oldGenerator = new();
    private readonly CrashSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoExceptions_EmitsInfoFinding() => AssertParity(
        CrashResult(totalExceptions: 0, activeExceptions: 0, candidates: []));

    [Fact]
    public Task NoActiveExceptions_EmitsWarningFinding() => AssertParity(
        CrashResult(totalExceptions: 10, activeExceptions: 0, candidates: []));

    [Fact]
    public Task ActiveExceptions_WithTopCandidate_UsesCandidatePrimaryType() => AssertParity(
        CrashResult(totalExceptions: 13, activeExceptions: 3, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, "FooException"),
        ]));

    [Fact]
    public Task ActiveExceptions_MultipleCandidates_UsesHighestActiveCount() => AssertParity(
        CrashResult(totalExceptions: 14, activeExceptions: 4, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, "FooException"),
            Candidate(threadId: 2, activeCount: 1, "BarException"),
        ]));

    [Fact]
    public Task ActiveExceptions_NoCandidates_FallsBackToActiveExceptionTypeCounts() => AssertParity(
        CrashResult(totalExceptions: 13, activeExceptions: 3, candidates: [], activeExceptionTypeName: "FooException"));

    private static CrashDomainResult CrashResult(
        int totalExceptions, int activeExceptions, IReadOnlyList<CrashThreadCandidateSnapshot> candidates, string activeExceptionTypeName = "FooException") => new(
        TotalExceptions: totalExceptions,
        ActiveExceptions: activeExceptions,
        ExceptionTypeCounts: new Dictionary<string, int> { ["FooException"] = totalExceptions },
        ActiveExceptionTypeCounts: activeExceptions > 0 ? new Dictionary<string, int> { [activeExceptionTypeName] = activeExceptions } : new Dictionary<string, int>(),
        TopCrashThreadCandidates: candidates);

    private static CrashThreadCandidateSnapshot Candidate(uint threadId, int activeCount, string primaryExceptionType) => new(
        ThreadId: threadId,
        OSThreadId: threadId,
        ActiveExceptionCount: activeCount,
        PrimaryExceptionType: primaryExceptionType,
        TopFrames: [],
        OriginalStackTrace: null,
        OriginalStackTraceInferred: false,
        OriginalStackTraceInferredFrom: null);

    private async Task AssertParity(CrashDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(CrashDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.RuntimeThreads };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.crash-population",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(result.TotalExceptions, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["active.count"] = new Measure(result.ActiveExceptions, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["unique-types.count"] = new Measure(result.ExceptionTypeCounts.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (CrashThreadCandidateSnapshot candidate in result.TopCrashThreadCandidates ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.crash-thread-candidate",
                Subjects = [new TypeRef { CanonicalName = candidate.PrimaryExceptionType, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["active-exception.count"] = new Measure(candidate.ActiveExceptionCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        // Only emitted here when there are no candidates, mirroring the fallback scenario the rule
        // and old generator both handle (real production always has one runtime.active-exception
        // per active exception regardless of candidates, but that population isn't needed to prove
        // the fallback path when candidates are absent).
        if ((result.TopCrashThreadCandidates?.Count ?? 0) == 0)
        {
            foreach (KeyValuePair<string, int> kv in result.ActiveExceptionTypeCounts)
            {
                for (int i = 0; i < kv.Value; i++)
                {
                    observations.Add(new Observation
                    {
                        Id = ObservationId.NewId(),
                        ObservationType = "runtime.active-exception",
                        Subjects = [new TypeRef { CanonicalName = kv.Key, Fidelity = MatchFidelity.Exact }],
                        When = Point(),
                        Measures = new Dictionary<string, Measure>
                        {
                            ["generation.ordinal"] = new Measure(2, MeasureUnit.Custom, MeasureSemantics.Absolute),
                            ["shallow.bytes"] = new Measure(64, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        },
                        Provenance = Provenance(capabilitiesUsed),
                        Confidence = 1.0,
                    });
                }
            }
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Crash Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
