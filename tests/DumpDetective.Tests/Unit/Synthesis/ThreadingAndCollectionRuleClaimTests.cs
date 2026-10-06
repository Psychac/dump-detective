using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 2: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class ThreadingAndCollectionRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── ThreadStackCluster ───────────────────────────────────────────────────

    [Fact]
    public async Task ThreadStackCluster_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ThreadStackClusterSynthesisRule(),
        [
            new("low-diversity-no-dominant-cluster", [Diversity(alive: 40, unique: 5, percent: 12.5)]),
            new("low-diversity-application-hotspot", [Diversity(40, 8, 20), Cluster(count: 30, signature: "App.Service.Wait")]),
            new("dominant-framework-idle-pattern", [Diversity(40, 8, 20), Cluster(30, "System.Threading.Monitor.Wait", frameworkPattern: "Monitor idle wait")]),
            new("healthy-diversity", [Diversity(40, 24, 60), Cluster(10, "App.Handler.Run")]),
        ]);
    }

    [Fact]
    public async Task ThreadStackCluster_DominantClaim_IsAHotspotOnlyWhenNotAnIdlePattern()
    {
        IReadOnlyList<Claim> hotspot = await RunAsync(new ThreadStackClusterSynthesisRule(), [Diversity(40, 8, 20), Cluster(30, "App.Service.Wait")]);
        IReadOnlyList<Claim> idle = await RunAsync(new ThreadStackClusterSynthesisRule(), [Diversity(40, 8, 20), Cluster(30, "Monitor.Wait", "Monitor idle wait")]);

        Claim hotspotClaim = hotspot.Single(c => c.Title == "Dominant thread cluster");
        Claim idleClaim = idle.Single(c => c.Title == "Dominant thread cluster");

        hotspotClaim.Assertion!.Predicate.Should().Be(AssertionPredicate.Dominates);
        hotspotClaim.Assertion.Measure!.Value.Value.Should().Be(75);
        idleClaim.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        idleClaim.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("Monitor idle wait");
    }

    // ── Hang ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hang_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new HangSynthesisRule(),
        [
            new("info", [HangPressure(waitingPercent: 10, queued: 0)]),
            new("info-with-faulted-tasks", [HangPressure(10, 0, faulted: 4)]),
            new("warning-by-waiting-pool-busy", [HangPressure(60, 0, cpu: 70), WaitState(circular: 0)]),
            new("warning-by-queue-alone", [HangPressure(20, 600)]),
            new("critical-pool-quiet", [HangPressure(85, 0, cpu: 10), WaitState(circular: 1), WaitState(circular: 0)]),
        ]);
    }

    [Theory]
    [InlineData(10, 0, AssertionPredicate.Normal, 50.0, "warning waiting threshold")]
    [InlineData(60, 0, AssertionPredicate.Stalls, 50.0, "warning waiting threshold")]
    [InlineData(85, 0, AssertionPredicate.Stalls, 80.0, "critical waiting threshold")]
    [InlineData(20, 600, AssertionPredicate.Stalls, 500.0, "queued work item threshold")]
    public async Task Hang_ReferencesWhicheverThresholdProducedTheSeverity(
        double waitingPercent, int queued, AssertionPredicate predicate, double budget, string basis)
    {
        IReadOnlyList<Claim> claims = await RunAsync(new HangSynthesisRule(), [HangPressure(waitingPercent, queued)]);

        Claim claim = claims.Should().ContainSingle().Subject;
        claim.Assertion!.Predicate.Should().Be(predicate);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
        claim.Assertion.Reference.Basis.Should().Be(basis);
    }

    [Fact]
    public async Task Hang_CounterIsEvidenceOnlyWhenTheThreadPoolIsBusy()
    {
        Claim busy = (await RunAsync(new HangSynthesisRule(), [HangPressure(60, 0, cpu: 70)])).Single();
        Claim quiet = (await RunAsync(new HangSynthesisRule(), [HangPressure(60, 0, cpu: 10)])).Single();

        busy.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("70%");
        quiet.Counter!.Statements.Should().BeEmpty();
        quiet.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
    }

    // ── Collection ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Collection_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new CollectionSynthesisRule(),
        [
            new("warning", [CollectionSummary(wastedBytes: 60 * Mb, wasteful: 12), KindWaste("List", 40 * Mb), ElementWaste("System.String", 30 * Mb)]),
            new("info-with-some-waste", [CollectionSummary(10 * Mb, 3)]),
            new("info-no-waste", [CollectionSummary(0, 0)]),
        ]);
    }

    [Theory]
    [InlineData(60, AssertionPredicate.Exceeds)]
    [InlineData(10, AssertionPredicate.Normal)]
    public async Task Collection_ComparesWastedBytesToTheWarningThreshold(long wastedMb, AssertionPredicate predicate)
    {
        Claim claim = (await RunAsync(new CollectionSynthesisRule(), [CollectionSummary(wastedMb * Mb, 3)])).Single();

        claim.Assertion!.Predicate.Should().Be(predicate);
        claim.Assertion.Measure!.Value.Value.Should().Be(wastedMb * Mb);
        claim.Assertion.Reference!.Value.Value.Should().Be(50 * Mb);
    }

    [Fact]
    public async Task Collection_SupportCitesTheBreakdownsOnlyWhenTheyExist()
    {
        Claim withBreakdowns = (await RunAsync(new CollectionSynthesisRule(), [CollectionSummary(60 * Mb, 3), KindWaste("List", 40 * Mb), ElementWaste("System.String", 30 * Mb)])).Single();
        Claim summaryOnly = (await RunAsync(new CollectionSynthesisRule(), [CollectionSummary(60 * Mb, 3)])).Single();

        withBreakdowns.Support.Should().HaveCount(3);
        summaryOnly.Support.Should().ContainSingle();
    }

    // ── TimerLeak ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TimerLeak_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new TimerLeakSynthesisRule(),
        [
            new("logical-warning-with-root-path", [TimerType("System.Threading.TimerQueueTimer", 100), TimerPopulation()]),
            new("logical-critical-and-queue-warning", [TimerType("System.Threading.TimerQueueTimer", 250), TimerType("System.Threading.TimerHolder", 100)]),
            new("queue-notice-only", [TimerType("System.Threading.TimerHolder", 60)]),
        ]);
    }

    [Fact]
    public async Task TimerLeak_EmitsTheLogicalAndQueueClaimsIndependently()
    {
        IReadOnlyList<Claim> both = await RunAsync(new TimerLeakSynthesisRule(), [TimerType("System.Threading.TimerQueueTimer", 250), TimerType("System.Threading.TimerHolder", 100)]);
        IReadOnlyList<Claim> queueOnly = await RunAsync(new TimerLeakSynthesisRule(), [TimerType("System.Threading.TimerHolder", 60)]);

        both.Should().HaveCount(2);
        both.Single(c => c.Fingerprint.EndsWith("logical-timers")).Assertion!.Reference!.Basis.Should().Be("critical threshold");
        both.Single(c => c.Fingerprint.EndsWith("queue-pressure")).Assertion!.Reference!.Basis.Should().Be("warning threshold");
        queueOnly.Should().ContainSingle().Which.Assertion!.Reference!.Basis.Should().Be("notice threshold");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation Diversity(int alive, int unique, double percent) =>
        RuleShapeGolden.Observation("runtime.thread-cluster-diversity", "Thread Stack Signature Clustering", new()
        {
            ["alive-thread.count"] = Count(alive),
            ["unique-cluster.count"] = Count(unique),
            ["diversity.percent"] = Percent(percent),
        });

    private static Observation Cluster(int count, string signature, string? frameworkPattern = null) =>
        RuleShapeGolden.Observation("thread.stack-cluster", "Thread Stack Signature Clustering", new() { ["cluster.count"] = Count(count) }) with
        {
            Evidence = frameworkPattern is null ? [Describe(signature)] : [Describe(signature), Describe(frameworkPattern)],
        };

    private static Observation HangPressure(double waitingPercent, int queued, int cpu = 10, int faulted = 0) =>
        RuleShapeGolden.Observation("runtime.hang-pressure", "Hang Analysis", new()
        {
            ["waiting-thread.count"] = Count(waitingPercent),
            ["alive-thread.count"] = Count(100),
            ["waiting.percent"] = Percent(waitingPercent),
            ["queued-work-items.count"] = Count(queued),
            ["pending-task.count"] = Count(0),
            ["faulted-task.count"] = Count(faulted),
            ["task-continuation.count"] = Count(0),
            ["threadpool-max.count"] = Count(32),
            ["threadpool-active.count"] = Count(8),
            ["threadpool-cpu.percent"] = Percent(cpu),
            ["high-threadpool-threshold.count"] = Count(500),
        });

    private static Observation WaitState(int circular) =>
        RuleShapeGolden.Observation("thread.wait-state", "Hang Analysis", new() { ["circular-wait-candidate.count"] = Count(circular) });

    private static Observation CollectionSummary(long wastedBytes, int wasteful) =>
        RuleShapeGolden.Observation("collection.waste-summary", "Collection Analysis", new()
        {
            ["total-collection.count"] = Count(1_000),
            ["total-wasted.bytes"] = Bytes(wastedBytes),
            ["wasteful-collection.count"] = Count(wasteful),
        });

    private static Observation KindWaste(string kind, long wasteBytes) =>
        RuleShapeGolden.Observation("collection.kind-waste", "Collection Analysis", new()
        {
            ["waste.bytes"] = Bytes(wasteBytes),
            ["waste.count"] = Count(5),
        }) with { Evidence = [Describe(kind)] };

    private static Observation ElementWaste(string typeName, long wasteBytes) =>
        RuleShapeGolden.Observation("collection.element-type-waste", "Collection Analysis", new() { ["waste.bytes"] = Bytes(wasteBytes) }) with
        {
            Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation TimerType(string typeName, long instances) =>
        RuleShapeGolden.Observation("infra.timer", "Timer Leak Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation TimerPopulation() =>
        RuleShapeGolden.Observation("infra.timer-population", "Timer Leak Analysis", new()
        {
            ["top-type.has-root-path"] = Count(1),
            ["top-type.is-root-path-search-truncated"] = Count(0),
            ["top-type.evidence-signal.count"] = Count(1),
        });
}
