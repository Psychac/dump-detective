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
/// Field-for-field parity between <see cref="ThreadFindingGenerator"/> (old) and
/// <see cref="ThreadSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>ThreadAnalyzer.EmitSummaryObservationsAsync</c>: one whole-run
/// <c>runtime.thread-triage</c> observation carrying every scalar the old generator read directly
/// off <c>ThreadDomainResult</c>, plus an optional <c>runtime.finalizer-thread</c> observation only
/// when a finalizer thread was found.
/// </remarks>
public sealed class ThreadSynthesisRuleParityTests
{
    private readonly ThreadFindingGenerator _oldGenerator = new();
    private readonly ThreadSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task HealthyState_OnlyEmitsInfoSummaryFinding() => AssertParity(BuildResult());

    [Fact]
    public Task ManyBlockedThreads_SummaryIsWarning() => AssertParity(BuildResult(
        aliveThreadCount: 20, blockedThreadCount: 10));

    [Fact]
    public Task ThreadsWithActiveExceptions_SummaryIsWarning() => AssertParity(BuildResult(
        threadsWithActiveExceptionsCount: 2));

    [Fact]
    public Task FinalizerBlocked_EmitsCriticalFinalizerFinding() => AssertParity(BuildResult(
        finalizerThreadBlocked: true, finalizerLockCount: 2, finalizerManagedThreadId: 7));

    [Fact]
    public Task FinalizerPresentButNotBlocked_NoFinalizerFinding() => AssertParity(BuildResult(
        finalizerThreadPresent: true, finalizerThreadBlocked: false));

    [Fact]
    public Task HighBlockedRatio_EmitsCriticalStarvationFinding() => AssertParity(BuildResult(
        aliveThreadCount: 10, blockedThreadCount: 8));

    [Fact]
    public Task BlockedRatioAtThreshold_DoesNotEmitStarvationFinding() => AssertParity(BuildResult(
        aliveThreadCount: 10, blockedThreadCount: 7));

    [Fact]
    public Task ZeroActiveThreads_EmitsCriticalHangFinding() => AssertParity(BuildResult(
        aliveThreadCount: 5, activeProcessingThreadCount: 0));

    [Fact]
    public Task NoAliveThreads_DoesNotEmitZeroActiveThreadsFinding() => AssertParity(BuildResult(
        aliveThreadCount: 0, activeProcessingThreadCount: 0));

    [Fact]
    public Task DeepAsyncChain_EmitsWarningFinding() => AssertParity(BuildResult(
        maxAsyncChainDepth: 15, asyncChainThreadCount: 3));

    [Fact]
    public Task AsyncChainAtThreshold_DoesNotEmitFinding() => AssertParity(BuildResult(
        maxAsyncChainDepth: 10));

    [Fact]
    public Task AllConditionsTriggered_EmitsAllFiveFindings() => AssertParity(BuildResult(
        aliveThreadCount: 10,
        blockedThreadCount: 8,
        finalizerThreadBlocked: true,
        finalizerLockCount: 1,
        activeProcessingThreadCount: 0,
        maxAsyncChainDepth: 20,
        asyncChainThreadCount: 4));

    private static ThreadDomainResult BuildResult(
        int aliveThreadCount = 10,
        int blockedThreadCount = 0,
        bool finalizerThreadPresent = false,
        bool finalizerThreadBlocked = false,
        int finalizerLockCount = 0,
        uint? finalizerManagedThreadId = null,
        int? activeProcessingThreadCount = null,
        int maxAsyncChainDepth = 0,
        int asyncChainThreadCount = 0,
        int threadsWithActiveExceptionsCount = 0) => new ThreadDomainResult(
        TotalThreadCount: aliveThreadCount,
        AliveThreadCount: aliveThreadCount,
        InactiveThreadCount: 0,
        GcThreadCount: 0,
        BlockedThreadCount: blockedThreadCount,
        LockHoldingThreadCount: 0,
        ThreadsWithActiveExceptionsCount: threadsWithActiveExceptionsCount,
        BackgroundThreadCount: 0,
        WaitPatternBreakdown: new Dictionary<string, int>(),
        TopActiveThreadHotspots: activeProcessingThreadCount.HasValue
            ? (activeProcessingThreadCount.Value > 0 ? [new NameCountEntry("MyApp.Worker.Run()", activeProcessingThreadCount.Value)] : [])
            : [new NameCountEntry("MyApp.Worker.Run()", aliveThreadCount)],
        FinalizerThreadBlocked: finalizerThreadBlocked,
        FinalizerLockCount: finalizerLockCount,
        FinalizerManagedThreadId: finalizerThreadBlocked || finalizerThreadPresent ? finalizerManagedThreadId ?? 1 : finalizerManagedThreadId,
        BlockedThreadRatio: aliveThreadCount > 0 ? (double)blockedThreadCount / aliveThreadCount : 0.0,
        MaxAsyncChainDepth: maxAsyncChainDepth,
        AsyncChainThreadCount: asyncChainThreadCount);

    private async Task AssertParity(ThreadDomainResult result)
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

    // ── Mirrors ThreadAnalyzer.EmitSummaryObservationsAsync ──────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(ThreadDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.RuntimeThreads };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.thread-triage",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["alive-thread.count"] = new Measure(result.AliveThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["blocked-thread.count"] = new Measure(result.BlockedThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["lock-holding-thread.count"] = new Measure(result.LockHoldingThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["active-exception-thread.count"] = new Measure(result.ThreadsWithActiveExceptionsCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["active-processing-thread.count"] = new Measure(SumHotspotCounts(result.TopActiveThreadHotspots), MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["async-chain-thread.count"] = new Measure(result.AsyncChainThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["max-async-chain.depth"] = new Measure(result.MaxAsyncChainDepth, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        if (result.FinalizerManagedThreadId.HasValue)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.finalizer-thread",
                Subjects = [new ThreadRef { OsThreadId = 1, ManagedThreadId = (int)result.FinalizerManagedThreadId.Value, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["is-blocked"] = new Measure(result.FinalizerThreadBlocked ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["locks-held.count"] = new Measure(result.FinalizerLockCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static int SumHotspotCounts(IReadOnlyList<NameCountEntry>? entries)
    {
        if (entries is null) return 0;
        int total = 0;
        for (int i = 0; i < entries.Count; i++) total += entries[i].Count;
        return total;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Thread Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
