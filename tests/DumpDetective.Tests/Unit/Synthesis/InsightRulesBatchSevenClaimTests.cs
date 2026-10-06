using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, final cross-analyzer group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchSevenClaimTests
{
    private const long Mb = 1_048_576;
    private const long Gb = 1_073_741_824;

    [Fact]
    public async Task AddressSpacePressure_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AddressSpacePressureInsightRule(),
        [
            new("ratio-and-ephemeral-fill", [Reservation(ratio: 5, fill: 95), Topology(segments: 12)]),
            new("32-bit-by-reservation", [Reservation(ratio: 1, pointerSize: 4, reserved: 3 * Gb)]),
            new("quiet-says-nothing", [Reservation(1.1, fill: 40)]),
        ]);
    }

    [Fact]
    public async Task AddressSpacePressure_EphemeralClaimCitesTheTopologyOnlyWhenPresent()
    {
        Claim withTopology = (await RunAsync(new AddressSpacePressureInsightRule(), [Reservation(1.1, fill: 95), Topology(12)])).Single();
        Claim without = (await RunAsync(new AddressSpacePressureInsightRule(), [Reservation(1.1, fill: 95)])).Single();

        withTopology.Support.Should().HaveCount(2);
        without.Support.Should().ContainSingle();
    }

    [Fact]
    public async Task AllocationPressureCrossCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AllocationPressureCrossCorrelationInsightRule(),
        [
            new("critical-with-blocked-threads", [Allocation(gen0: 0.10, gen2: 0.80, loh: 0.20), Triage(alive: 10, blocked: 4)]),
            new("critical-without-thread-impact", [Allocation(0.10, 0.80, 0.20), Triage(10, 1)]),
            new("high", [Allocation(0.20, 0.40, 0.10)]),
            new("moderate-says-nothing", [Allocation(0.75, 0.60, 0.30)]),
        ]);
    }

    [Fact]
    public async Task AllocationPressureCrossCorrelation_CitesTheThreadTriageOnlyWhenBlockedThreadsMatter()
    {
        Claim impacted = (await RunAsync(new AllocationPressureCrossCorrelationInsightRule(), [Allocation(0.10, 0.80, 0.20), Triage(10, 4)])).Single();
        Claim notImpacted = (await RunAsync(new AllocationPressureCrossCorrelationInsightRule(), [Allocation(0.10, 0.80, 0.20), Triage(10, 1)])).Single();

        impacted.Support.Should().HaveCount(2);
        impacted.Assertion!.Qualifiers.Mechanism.Should().Contain("blocked threads");
        notImpacted.Support.Should().ContainSingle();
    }

    [Fact]
    public async Task ClusterHangCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ClusterHangCorrelationInsightRule(),
        [
            new("overlap-with-diversity", [Cluster(rank: 0, count: 30, "App.Service.Wait"), Member(0, 1), Member(0, 2), Member(0, 3), WaitState(1), WaitState(2), WaitState(3), Diversity(alive: 100)]),
            new("overlap-with-a-deadlock-candidate", [Cluster(0, 60, "App.Service.Wait"), Member(0, 1), Member(0, 2), WaitState(1), WaitState(2), Blocked(1, "App.Lock"), Diversity(100)]),
            new("too-little-overlap-says-nothing", [Cluster(0, 30, "App.Service.Wait"), Member(0, 1), Member(0, 2), Member(0, 3), WaitState(1), Diversity(100)]),
        ]);
    }

    [Fact]
    public async Task ClusterHangCorrelation_CitesClusterWaitStatesAndDeadlockCandidatesSeparately()
    {
        Claim claim = (await RunAsync(new ClusterHangCorrelationInsightRule(),
            [Cluster(0, 60, "App.Service.Wait"), Member(0, 1), Member(0, 2), WaitState(1), WaitState(2), Blocked(1, "App.Lock"), Diversity(100)])).Single();

        claim.Support.Should().HaveCount(3);
        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Stalls);
        claim.Assertion.Measure!.Value.Value.Should().Be(2);
    }

    [Fact]
    public async Task CrashModuleHotspot_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new CrashModuleHotspotInsightRule(),
        [
            new("large-on-disk", [CrashModule("Contoso.Core", 6), CrashModule("Contoso.Other", 1), DiskFootprint("Contoso.Core", 40 * Mb)]),
            new("in-conflict", [CrashModule("Contoso.Core", 6), Conflict("Contoso.Core")]),
            new("exactly-half-is-still-dominant", [CrashModule("Contoso.Core", 3), CrashModule("Contoso.Other", 3), Conflict("Contoso.Core")]),
            new("below-half-says-nothing", [CrashModule("Contoso.Core", 3), CrashModule("Contoso.Other", 2), CrashModule("Contoso.Third", 2), Conflict("Contoso.Core")]),
            new("uncorrelated-says-nothing", [CrashModule("Contoso.Core", 6)]),
        ]);
    }

    [Fact]
    public async Task KnownLeakPatterns_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new KnownLeakPatternsInsightRule(),
        [
            new("both-patterns", [KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass1", 1_500), KnownPattern("System.Reflection.RuntimeMethodInfo", 60_000)]),
            new("closures-only", [KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass1", 1_500)]),
            new("below-thresholds-says-nothing", [KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass1", 10), KnownPattern("System.Reflection.RuntimeMethodInfo", 100)]),
        ]);
    }

    [Fact]
    public async Task ThreadContention_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ThreadContentionInsightRule(),
        [
            new("hang-pressure-critical", [HangPressure(waitingPercent: 80, waiting: 80), Triage(100, 10)]),
            new("blocked-warning-with-finalizer-running", [Triage(10, 6), FinalizerThread(blocked: false)]),
            new("blocked-critical-with-finalizer-blocked", [Triage(10, 8), FinalizerThread(blocked: true)]),
            new("finalizer-blocked-alone", [Triage(10, 1), FinalizerThread(true)]),
            new("quiet-says-nothing", [Triage(10, 1), FinalizerThread(false)]),
        ]);
    }

    [Fact]
    public async Task ThreadContention_FinalizerClaimNamesTheFinalizerThreadAsItsSubject()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new ThreadContentionInsightRule(), [Triage(10, 1), FinalizerThread(true)]);

        Claim finalizer = claims.Single(c => c.Fingerprint.EndsWith("finalizer-blocked"));
        finalizer.Assertion!.Subject.Should().BeOfType<ThreadRef>().Which.OsThreadId.Should().Be(4_100u);
        finalizer.Assertion.Predicate.Should().Be(AssertionPredicate.Blocks);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static ThreadRef Thread(uint osThreadId) => new() { OsThreadId = osThreadId, ManagedThreadId = (int)osThreadId, Fidelity = MatchFidelity.Exact };

    private static ModuleRef Module(string name) => new() { SimpleName = name, Fidelity = MatchFidelity.Exact };

    private static Observation Reservation(double ratio, double fill = 40, int pointerSize = 8, long reserved = 6 * Gb) =>
        RuleShapeGolden.Observation("heap.segment-reservation", "Segment Reservation Analysis", new()
        {
            ["dump-pointer-size"] = Count(pointerSize),
            ["reserved.bytes"] = Bytes(reserved),
            ["committed.bytes"] = Bytes(2 * Gb),
            ["reserved-to-committed.ratio"] = Ratio(ratio),
            ["ratio-high-pressure.threshold"] = Ratio(4),
            ["thirty-two-bit-pressure.threshold-bytes"] = Bytes(2_000_000_000),
            ["ephemeral.segment-count"] = Count(2),
            ["ephemeral.avg-fill-percent"] = Percent(fill),
        });

    private static Observation Topology(long segments) =>
        RuleShapeGolden.Observation("heap.topology-population", "Heap Topology", new() { ["segment.count"] = Count(segments) });

    private static Observation Allocation(double gen0, double gen2, double loh) =>
        RuleShapeGolden.Observation("gc.allocation-composition", "Allocation Pattern Analysis", new()
        {
            ["gen0.count-fraction"] = Ratio(gen0),
            ["gen2.count-fraction"] = Ratio(gen2),
            ["gen0.size-fraction"] = Ratio(gen0),
            ["loh.size-fraction"] = Ratio(loh),
            ["transient-classification.threshold"] = Ratio(70),
        });

    private static Observation Triage(long alive, long blocked) =>
        RuleShapeGolden.Observation("runtime.thread-triage", "Thread Analysis", new()
        {
            ["alive-thread.count"] = Count(alive),
            ["blocked-thread.count"] = Count(blocked),
            ["lock-holding-thread.count"] = Count(1),
        });

    private static Observation Cluster(int rank, long count, string signature) =>
        RuleShapeGolden.Observation("thread.stack-cluster", "Thread Stack Signature Clustering", new()
        {
            ["cluster.rank"] = Count(rank),
            ["cluster.count"] = Count(count),
        }) with { Evidence = [Describe(signature)] };

    private static Observation Member(int rank, uint osThreadId) =>
        RuleShapeGolden.Observation("thread.stack-cluster-member", "Thread Stack Signature Clustering", new() { ["cluster.rank"] = Count(rank) }) with
        {
            Subjects = [Thread(osThreadId)],
        };

    private static Observation WaitState(uint osThreadId) =>
        RuleShapeGolden.Observation("thread.wait-state", "Hang Analysis", new() { ["wait-type.ordinal"] = Count(0) }) with { Subjects = [Thread(osThreadId)] };

    private static Observation Blocked(uint osThreadId, string lockTypes) =>
        RuleShapeGolden.Observation("thread.blocked", "Lock Graph Analysis", new()) with
        {
            Subjects = [Thread(osThreadId)],
            Evidence = [Describe(lockTypes)],
        };

    private static Observation Diversity(long alive) =>
        RuleShapeGolden.Observation("runtime.thread-cluster-diversity", "Thread Stack Signature Clustering", new() { ["alive-thread.count"] = Count(alive) });

    private static Observation CrashModule(string module, long active) =>
        RuleShapeGolden.Observation("runtime.crash-thread-candidate-module", "Crash Analysis", new() { ["active-exception.count"] = Count(active) }) with
        {
            Subjects = [Module(module)],
        };

    private static Observation DiskFootprint(string module, long bytes) =>
        RuleShapeGolden.Observation("runtime.module-disk-footprint", "Module Analysis", new() { ["disk.bytes"] = Bytes(bytes) }) with
        {
            Subjects = [Module(module)],
        };

    private static Observation Conflict(string module) =>
        RuleShapeGolden.Observation("runtime.module-conflict-group", "Module Analysis", new()) with { Subjects = [Module(module)] };

    private static Observation KnownPattern(string type, long instances) =>
        RuleShapeGolden.Observation("type.known-pattern-population", "Memory Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [new TypeRef { CanonicalName = type, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation HangPressure(double waitingPercent, long waiting) =>
        RuleShapeGolden.Observation("runtime.hang-pressure", "Hang Analysis", new()
        {
            ["waiting.percent"] = Percent(waitingPercent),
            ["waiting-thread.count"] = Count(waiting),
        });

    private static Observation FinalizerThread(bool blocked) =>
        RuleShapeGolden.Observation("runtime.finalizer-thread", "Thread Analysis", new()
        {
            ["is-blocked"] = Count(blocked ? 1 : 0),
            ["locks-held.count"] = Count(2),
        }) with { Subjects = [Thread(4_100)] };
}
