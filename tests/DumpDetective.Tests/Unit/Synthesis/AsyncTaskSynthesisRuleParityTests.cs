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
/// Field-for-field parity between <see cref="AsyncTaskFindingGenerator"/> (old) and
/// <see cref="AsyncTaskSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class AsyncTaskSynthesisRuleParityTests
{
    private readonly AsyncTaskFindingGenerator _oldGenerator = new();
    private readonly AsyncTaskSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoSignals_ReturnsEmpty() => AssertParity(BuildResult());

    [Fact]
    public Task SingleSignal_Orphaned() => AssertParity(BuildResult(total: 200, pending: 120, orphaned: 12));

    [Fact]
    public Task MultipleSignals_ReturnsAggregatePlusTopDetail() => AssertParity(BuildResult(
        total: 2000, pending: 1800, faulted: 120, orphaned: 420, maxDepth: 17, avgDepth: 8.4));

    [Fact]
    public Task AllSignals_StillCapsAtTwoFindings() => AssertParity(BuildResult(
        total: 10000, pending: 7000, faulted: 300, orphaned: 500, maxDepth: 25, avgDepth: 12.5,
        cycleDetected: true, pendingGen2: 1000, pendingLoh: 500,
        totalTcs: 100, unresolvedTcs: 40, unresolvedTcsGen2: 25,
        totalVts: 100, pendingVts: 40, pendingVtsGen2: 25));

    [Fact]
    public Task CycleDetected() => AssertParity(BuildResult(cycleDetected: true));

    [Fact]
    public Task PendingOldGenAboveWarningThreshold() => AssertParity(BuildResult(
        pending: 100, pendingGen2: 40, pendingLoh: 20));

    [Fact]
    public Task PendingOldGenBelowWarningThreshold() => AssertParity(BuildResult(
        pending: 100, pendingGen2: 5, pendingLoh: 0));

    [Fact]
    public Task UnresolvedTcsGen2AtOrAboveWarningThreshold() => AssertParity(BuildResult(
        totalTcs: 100, unresolvedTcs: 40, unresolvedTcsGen2: 20));

    [Fact]
    public Task UnresolvedTcsGen2BelowWarningThreshold() => AssertParity(BuildResult(
        totalTcs: 100, unresolvedTcs: 40, unresolvedTcsGen2: 5));

    [Fact]
    public Task PendingVtsGen2AtOrAboveWarningThreshold() => AssertParity(BuildResult(
        totalVts: 100, pendingVts: 40, pendingVtsGen2: 20));

    [Fact]
    public Task PendingVtsGen2BelowWarningThreshold() => AssertParity(BuildResult(
        totalVts: 100, pendingVts: 40, pendingVtsGen2: 5));

    [Fact]
    public Task NoPendingVtsGen2_NoSignal() => AssertParity(BuildResult(
        totalVts: 50, pendingVts: 50, pendingVtsGen2: 0));

    [Fact]
    public Task OrphanedCriticalByAbsoluteCount() => AssertParity(BuildResult(total: 1000, orphaned: 100));

    [Fact]
    public Task OrphanedCriticalByPercentage() => AssertParity(BuildResult(total: 100, orphaned: 25));

    [Fact]
    public Task OrphanedWarning() => AssertParity(BuildResult(total: 1000, orphaned: 15));

    [Fact]
    public Task OrphanedInfo() => AssertParity(BuildResult(total: 1000, orphaned: 3));

    [Fact]
    public Task FaultedTasks() => AssertParity(BuildResult(total: 500, faulted: 10));

    [Fact]
    public Task ContinuationDepthWarning() => AssertParity(BuildResult(maxDepth: 20, avgDepth: 10.0));

    [Fact]
    public Task ContinuationDepthInfo() => AssertParity(BuildResult(maxDepth: 12, avgDepth: 6.0));

    [Fact]
    public Task ContinuationDepthBelowThreshold_NoSignal() => AssertParity(BuildResult(maxDepth: 5, avgDepth: 2.0));

    [Fact]
    public Task PendingCritical_ByAbsoluteCount() => AssertParity(BuildResult(total: 10000, pending: 6000));

    [Fact]
    public Task PendingCritical_ByRate() => AssertParity(BuildResult(total: 1000, pending: 800));

    [Fact]
    public Task PendingWarning_ByRate() => AssertParity(BuildResult(total: 2000, pending: 1200));

    [Fact]
    public Task PendingWarning_ByAbsoluteCount() => AssertParity(BuildResult(total: 20000, pending: 2500));

    [Fact]
    public Task PendingInfo() => AssertParity(BuildResult(total: 100000, pending: 600));

    [Fact]
    public Task PendingAtOrBelowThreshold_NoSignal() => AssertParity(BuildResult(total: 1000, pending: 500));

    private async Task AssertParity(AsyncTaskDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(AsyncTaskDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.DumpObjectFields };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "async.task-population",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(result.TotalTasks, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pending.count"] = new Measure(result.PendingTasks, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pending-oldgen.count"] = new Measure(result.PendingGen2 + result.PendingLOH, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["faulted.count"] = new Measure(result.FaultedTasks, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["cycle-detected"] = new Measure(result.CycleDetected ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["max-continuation-depth"] = new Measure(result.MaxContinuationDepth, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["avg-continuation-depth"] = new Measure(result.AvgContinuationDepth, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-tcs.count"] = new Measure(result.TotalTaskCompletionSources, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["unresolved-tcs.count"] = new Measure(result.UnresolvedTaskCompletionSources, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["unresolved-tcs-oldgen.count"] = new Measure(result.UnresolvedTcsGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-vts.count"] = new Measure(result.TotalValueTaskSources, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pending-vts.count"] = new Measure(result.PendingValueTaskSources, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pending-vts-oldgen.count"] = new Measure(result.PendingVtsGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        for (int i = 0; i < result.OrphanedTasks; i++)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.orphaned-task",
                Subjects = [new TypeRef { CanonicalName = "System.Threading.Tasks.Task", Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["shallow.bytes"] = new Measure(64, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Async Task Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static AsyncTaskDomainResult BuildResult(
        int total = 100,
        int pending = 0,
        int faulted = 0,
        int orphaned = 0,
        int maxDepth = 0,
        double avgDepth = 0.0,
        bool cycleDetected = false,
        int pendingGen2 = 0,
        int pendingLoh = 0,
        int totalTcs = 0,
        int unresolvedTcs = 0,
        int unresolvedTcsGen2 = 0,
        int totalVts = 0,
        int pendingVts = 0,
        int pendingVtsGen2 = 0)
    {
        int completed = total - pending;
        if (completed < 0) completed = 0;

        return new AsyncTaskDomainResult(
            TotalTasks: total,
            PendingTasks: pending,
            RunningTasks: pending,
            FaultedTasks: faulted,
            CanceledTasks: 0,
            CompletedTasks: completed,
            OrphanedTasks: orphaned,
            TotalTaskContinuations: total * 2,
            MaxContinuationDepth: maxDepth,
            AvgContinuationDepth: avgDepth,
            TopPendingTaskTypes: [],
            TopFaultedTaskTypes: [],
            TopContinuationTypes: [],
            TopOrphanedTasks: [],
            TopDeepestChains: [],
            CycleDetected: cycleDetected,
            PendingGen2: pendingGen2,
            PendingLOH: pendingLoh,
            TotalTaskCompletionSources: totalTcs,
            UnresolvedTaskCompletionSources: unresolvedTcs,
            UnresolvedTcsGen2Count: unresolvedTcsGen2,
            TotalValueTaskSources: totalVts,
            PendingValueTaskSources: pendingVts,
            PendingVtsGen2Count: pendingVtsGen2);
    }
}
