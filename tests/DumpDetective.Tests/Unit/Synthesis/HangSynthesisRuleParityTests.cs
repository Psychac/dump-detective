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
/// Field-for-field parity between <see cref="HangFindingGenerator"/> (old) and
/// <see cref="HangSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>HangAnalyzer.EmitObservationsAsync</c>: one
/// <c>runtime.hang-pressure</c> whole-run observation plus one <c>thread.wait-state</c> per
/// <c>TopWaitingThreads</c> entry, each carrying the raw <c>circular-wait-candidate.count</c> fact
/// the rule needs to reproduce <c>HangHealthClassifier.ComputeHealthScore</c>'s deadlock-candidate
/// penalty without WaitType ever reaching Reporting.
/// </remarks>
public sealed class HangSynthesisRuleParityTests
{
    private readonly HangFindingGenerator _oldGenerator = new();
    private readonly HangSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task WaitingPercentAtOrAboveEighty_EmitsCriticalFinding() => AssertParity(BuildResult(waitingPercent: 85.0));

    [Fact]
    public Task WaitingPercentAtOrAboveFifty_EmitsWarningFinding() => AssertParity(BuildResult(waitingPercent: 60.0));

    [Fact]
    public Task QueuedWorkItemsAboveThreshold_EmitsWarningFinding() => AssertParity(
        BuildResult(waitingPercent: 5.0, queuedWorkItems: 600));

    [Fact]
    public Task LowWaitingPercent_LowQueue_EmitsInfoFinding() => AssertParity(
        BuildResult(waitingPercent: 5.0, queuedWorkItems: 10));

    [Fact]
    public Task CircularWaitCandidates_LowerHealthScore() => AssertParity(BuildResult(
        waitingPercent: 5.0,
        topWaitingThreads:
        [
            new WaitingThreadSnapshot(1, 101, "MonitorWait", "Object.Wait", LockCount: 1, TopStackFrame: "Foo.Bar"),
            new WaitingThreadSnapshot(2, 102, "MonitorWait", "Object.Wait", LockCount: 1, TopStackFrame: "Foo.Baz"),
        ]));

    [Fact]
    public Task NonCircularWaits_DoNotLowerHealthScoreViaCircularPenalty() => AssertParity(BuildResult(
        waitingPercent: 5.0,
        topWaitingThreads:
        [
            new WaitingThreadSnapshot(1, 101, "SleepWaitJoin", "Thread.Sleep", LockCount: 0, TopStackFrame: "Foo.Bar"),
        ]));

    [Fact]
    public Task ThreadPoolSaturatedWithLowCpu_StarvationPenalty() => AssertParity(BuildResult(
        waitingPercent: 5.0, runtimeMaxThreads: 10, runtimeActiveWorkerThreads: 10, runtimeCpuUtilization: 5));

    [Fact]
    public Task ThreadPoolSaturatedWithHighCpu_SaturationPenaltyOnly() => AssertParity(BuildResult(
        waitingPercent: 5.0, runtimeMaxThreads: 10, runtimeActiveWorkerThreads: 10, runtimeCpuUtilization: 90));

    [Fact]
    public Task PendingTasksAboveHighThreadPoolThreshold_TaskBackpressurePenalty() => AssertParity(BuildResult(
        waitingPercent: 5.0, pendingTasks: 150, highThreadPoolThreshold: 100));

    [Fact]
    public Task FaultedTasksPresent_UnobservedFaultPenalty() => AssertParity(BuildResult(
        waitingPercent: 5.0, faultedTasks: 3));

    [Fact]
    public Task TaskContinuationsAboveOneThousand_BacklogPenalty() => AssertParity(BuildResult(
        waitingPercent: 5.0, totalTaskContinuations: 1500));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        waitingPercent: 85.0, queuedWorkItems: 600, runtimeMaxThreads: 10, runtimeActiveWorkerThreads: 10,
        runtimeCpuUtilization: 5, pendingTasks: 150, highThreadPoolThreshold: 100, faultedTasks: 3,
        totalTaskContinuations: 1500,
        topWaitingThreads:
        [
            new WaitingThreadSnapshot(1, 101, "MonitorWait", "Object.Wait", LockCount: 1, TopStackFrame: "Foo.Bar"),
            new WaitingThreadSnapshot(2, 102, "MonitorWait", "Object.Wait", LockCount: 1, TopStackFrame: "Foo.Baz"),
            new WaitingThreadSnapshot(3, 103, "MonitorWait", "Object.Wait", LockCount: 1, TopStackFrame: "Foo.Qux"),
        ]));

    private static HangDomainResult BuildResult(
        double waitingPercent,
        int queuedWorkItems = 0,
        int runtimeMaxThreads = 0,
        int runtimeActiveWorkerThreads = 0,
        int runtimeCpuUtilization = 0,
        int pendingTasks = 0,
        int highThreadPoolThreshold = 100,
        int faultedTasks = 0,
        int totalTaskContinuations = 0,
        IReadOnlyList<WaitingThreadSnapshot>? topWaitingThreads = null) => new(
        TotalAliveThreads: 100,
        WaitingThreadCount: (int)(100 * waitingPercent / 100.0),
        ThreadsHoldingLocks: 0,
        WaitingPercent: waitingPercent,
        WaitCategoryBreakdown: new Dictionary<string, int>(),
        TotalTaskContinuations: totalTaskContinuations,
        QueuedWorkItems: queuedWorkItems,
        TotalTasks: 0,
        PendingTasks: pendingTasks,
        FaultedTasks: faultedTasks,
        CanceledTasks: 0,
        RuntimeThreadPoolDataAvailable: false,
        RuntimeMinThreads: 0,
        RuntimeMaxThreads: runtimeMaxThreads,
        RuntimeActiveWorkerThreads: runtimeActiveWorkerThreads,
        RuntimeIdleWorkerThreads: 0,
        RuntimeRetiredWorkerThreads: 0,
        RuntimeQueueLength: null,
        RuntimeCpuUtilization: runtimeCpuUtilization,
        HighThreadPoolThreshold: highThreadPoolThreshold,
        TopWaitingThreads: topWaitingThreads ?? []);

    private async Task AssertParity(HangDomainResult result)
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

    // ── Mirrors HangAnalyzer.EmitObservationsAsync ───────────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(HangDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.RuntimeThreads };
        var observations = new List<Observation>();

        foreach (WaitingThreadSnapshot wt in result.TopWaitingThreads ?? [])
        {
            bool isCircularWaitCandidate = wt.WaitType == "MonitorWait" && wt.LockCount > 0;

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "thread.wait-state",
                Subjects = [new ThreadRef { OsThreadId = wt.OSThreadId, ManagedThreadId = (int)wt.ThreadId, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["locks-held.count"] = new Measure(wt.LockCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["circular-wait-candidate.count"] = new Measure(isCircularWaitCandidate ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "runtime.hang-pressure",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["alive-thread.count"] = new Measure(result.TotalAliveThreads, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["waiting-thread.count"] = new Measure(result.WaitingThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["waiting.percent"] = new Measure(result.WaitingPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                ["queued-work-items.count"] = new Measure(result.QueuedWorkItems, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pending-task.count"] = new Measure(result.PendingTasks, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["faulted-task.count"] = new Measure(result.FaultedTasks, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["task-continuation.count"] = new Measure(result.TotalTaskContinuations, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["threadpool-max.count"] = new Measure(result.RuntimeMaxThreads, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["threadpool-active.count"] = new Measure(result.RuntimeActiveWorkerThreads, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["threadpool-cpu.percent"] = new Measure(result.RuntimeCpuUtilization, MeasureUnit.Percent, MeasureSemantics.Absolute),
                ["high-threadpool-threshold.count"] = new Measure(result.HighThreadPoolThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "Hang Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
