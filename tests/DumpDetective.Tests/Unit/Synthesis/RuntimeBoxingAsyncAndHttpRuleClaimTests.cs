using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 7: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class RuntimeBoxingAsyncAndHttpRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── Jit ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Jit_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new JitSynthesisRule(),
        [
            new("everything-fires", [JitActivity(heapBytes: 600 * Mb, managed: 100, unmanaged: 60, tiered: 5, large: 2, dynamic: 3, depth: 600)]),
            new("quiet", [JitActivity(10 * Mb, 100, 10, 0, 0, 0, 20)]),
        ]);
    }

    [Fact]
    public async Task Jit_OverviewDescribes_AndTieredCompilationAssertsNoProblem()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new JitSynthesisRule(), [JitActivity(10 * Mb, 100, 10, 5, 0, 0, 20)]);

        claims.Single(c => c.Fingerprint.EndsWith("overview")).Assertion!.Predicate.Should().Be(AssertionPredicate.Composes);
        claims.Single(c => c.Fingerprint.EndsWith("tiered-compilation")).Counter!.NoneFoundBecause.Should().Contain("asserts no problem");
    }

    [Fact]
    public async Task Jit_DeepStackNamesTheThreadItsSubject()
    {
        Claim deep = (await RunAsync(new JitSynthesisRule(), [JitActivity(10 * Mb, 100, 10, 0, 0, 0, 600, osThreadId: 4_100)]))
            .Single(c => c.Fingerprint.EndsWith("deep-stack"));

        deep.Assertion!.Subject.Should().BeOfType<ThreadRef>().Which.OsThreadId.Should().Be(4_100u);
    }

    // ── LeakCandidate ────────────────────────────────────────────────────────

    [Fact]
    public async Task LeakCandidate_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LeakCandidateSynthesisRule(),
        [
            new("critical-candidate", [Candidate("App.Cache", 200 * Mb, 9_000, 90, 5_000, finalizable: true, staticRooted: true, container: true), PinnedTarget("App.Cache"), DependentTarget("App.Cache")]),
            new("warning-aggregate", [Candidate("App.Cache", 200 * Mb, 9_000, 90, 5_000, finalizable: true, staticRooted: true)]),
            new("info-cluster-of-two", [Candidate("App.A", Mb, 10, 50, 5), Candidate("App.B", 2 * Mb, 20, 40, 8)]),
        ]);
    }

    [Fact]
    public async Task LeakCandidate_BothClaimKindsAdmitTheyAreHeuristics()
    {
        IReadOnlyList<Claim> critical = await RunAsync(new LeakCandidateSynthesisRule(),
            [Candidate("App.Cache", 200 * Mb, 9_000, 90, 5_000, finalizable: true, staticRooted: true, container: true), PinnedTarget("App.Cache"), DependentTarget("App.Cache")]);
        IReadOnlyList<Claim> aggregate = await RunAsync(new LeakCandidateSynthesisRule(), [Candidate("App.A", Mb, 10, 50, 5)]);

        critical.Single().Fingerprint.Should().Contain("critical.");
        critical.Single().Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("heuristics");
        aggregate.Single().Fingerprint.Should().EndWith("aggregate");
        aggregate.Single().Assertion!.Predicate.Should().Be(AssertionPredicate.Exhibits);
        aggregate.Single().Counter!.Statements.Should().ContainSingle();
    }

    // ── Boxing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Boxing_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new BoxingSynthesisRule(),
        [
            new("everything-fires",
            [
                BoxingPressure(total: 2_000_000, enums: 60_000, nullables: 20_000, oversized: 150_000, gen2: 1_800_000),
                BoxedType("App.Flags", enumType: true, equatable: false, instances: 40_000),
                BoxedType("App.Point", enumType: false, equatable: false, instances: 5_000),
                PaddingWaste("App.Wasteful", ratio: 0.5, wasted: 16, structSize: 32, fieldBytes: 16),
                OversizedType("App.Big", 128, 150_000),
            ]),
            new("quiet", [BoxingPressure(total: 10, enums: 0, nullables: 0, oversized: 0, gen2: 0)]),
        ]);
    }

    [Theory]
    [InlineData(1_500_000, "critical threshold", 1_000_000.0)]
    [InlineData(60_000, "warning threshold", 50_000.0)]
    [InlineData(5_000, "notice threshold", 1_000.0)]
    public async Task Boxing_EnumClaimReferencesTheThresholdThatSetItsSeverity(long enums, string basis, double budget)
    {
        Claim claim = (await RunAsync(new BoxingSynthesisRule(), [BoxingPressure(100, enums, 0, 0, 0)]))
            .Single(c => c.Fingerprint.EndsWith("boxed-enum"));

        claim.Assertion!.Reference!.Basis.Should().Be(basis);
        claim.Assertion.Reference.Value.Value.Should().Be(budget);
    }

    [Fact]
    public async Task Boxing_ClaimsCiteOnlyObservationsInTheirOwnLineage()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new BoxingSynthesisRule(),
            [BoxingPressure(2_000_000, 60_000, 20_000, 150_000, 1_800_000), BoxedType("App.Flags", true, false, 40_000), BoxedType("App.Point", false, false, 5_000)]);

        claims.Should().NotBeEmpty();
        claims.SelectMany(c => c.Support).SelectMany(s => s.Observations).Should().OnlyContain(id => claims.Any(c => c.DerivedFrom.Contains(id)));
    }

    // ── AsyncTask ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncTask_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AsyncTaskSynthesisRule(),
        [
            new("cycle", [TaskPopulation(cycle: true)]),
            new("pending-old-generation-warning", [TaskPopulation(total: 100, pending: 40, pendingOldGen: 30)]),
            new("unresolved-tcs-warning", [TaskPopulation(unresolvedTcsGen2: 25)]),
            new("pending-vts-info", [TaskPopulation(pendingVtsGen2: 3)]),
            new("orphaned", [TaskPopulation(total: 200), Orphan(), Orphan(), Orphan()]),
            new("faulted", [TaskPopulation(total: 100, faulted: 5)]),
            new("deep-continuation", [TaskPopulation(maxDepth: 16)]),
            new("pending-critical", [TaskPopulation(total: 4_000, pending: 3_000)]),
            new("cluster-faulted-and-pending", [TaskPopulation(total: 4_000, pending: 3_000, faulted: 5)]),
        ]);
    }

    [Fact]
    public async Task AsyncTask_ClusterClaimAccompaniesTheTopSignal_AndOrphanedCitesItsTasks()
    {
        IReadOnlyList<Claim> cluster = await RunAsync(new AsyncTaskSynthesisRule(), [TaskPopulation(total: 4_000, pending: 3_000, faulted: 5)]);
        Observation orphan = Orphan();
        IReadOnlyList<Claim> orphaned = await RunAsync(new AsyncTaskSynthesisRule(), [TaskPopulation(total: 200), orphan]);

        cluster.Should().HaveCount(2);
        cluster[0].Fingerprint.Should().EndWith("risk-cluster");
        cluster[1].Fingerprint.Should().EndWith("pending");
        orphaned.Single().Support.Single().Observations.Should().Equal(orphan.Id);
    }

    // ── HttpObject ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HttpObject_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new HttpObjectSynthesisRule(),
        [
            new("httpclient-per-request-critical", [HttpPopulation(), .. HttpObjects("System.Net.Http.HttpClient", 20, generation: 0)]),
            new("httpclient-long-lived", [HttpPopulation(), .. HttpObjects("System.Net.Http.HttpClient", 6, generation: 2)]),
            new("webrequest-and-response", [HttpPopulation(webResponses: 25), .. HttpObjects("System.Net.HttpWebRequest", 10)]),
            new("handlers-with-a-module", [HttpPopulation(handlers: 60), HandlerModule("Contoso.Http", 40)]),
            new("service-points", [HttpPopulation(), .. HttpObjects("System.Net.ServicePoint", 50, connectionLimit: 2)]),
            new("handler-tracking-churn", [HttpPopulation(), .. HttpObjects("Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry", 20), .. HttpObjects("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry", 4)]),
        ]);
    }

    [Fact]
    public async Task HttpObject_ClientCounterArguesLongLivedReuseOnlyWhenMostAreInGen2()
    {
        Claim longLived = (await RunAsync(new HttpObjectSynthesisRule(), [HttpPopulation(), .. HttpObjects("System.Net.Http.HttpClient", 6, generation: 2)])).Single();
        Claim perRequest = (await RunAsync(new HttpObjectSynthesisRule(), [HttpPopulation(), .. HttpObjects("System.Net.Http.HttpClient", 6, generation: 0)])).Single();

        longLived.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("long-lived reuse");
        perRequest.Counter!.Statements.Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation JitActivity(
        long heapBytes, long managed, long unmanaged, long tiered, long large, long dynamic, long depth, uint osThreadId = 100) =>
        RuleShapeGolden.Observation("runtime.jit-activity", "JIT Analysis", new()
        {
            ["jit-heap.bytes"] = Bytes(heapBytes),
            ["jit-manager.count"] = Count(3),
            ["active-methods.count"] = Count(managed),
            ["tiered-methods.count"] = Count(tiered),
            ["managed-frame.count"] = Count(managed),
            ["unmanaged-frame.count"] = Count(unmanaged),
            ["dynamic-method-frame.count"] = Count(dynamic),
            ["large-method.count"] = Count(large),
            ["large-method.threshold-bytes"] = Bytes(64 * 1024),
            ["max-thread-frame-depth.count"] = Count(depth),
            ["max-thread-frame-depth.os-thread-id"] = Count(osThreadId),
            ["top-method.hot-size-bytes"] = Bytes(100 * 1024),
            ["top-method.cold-size-bytes"] = Bytes(20 * 1024),
        }) with { Evidence = [Describe("App.Generated.Parse()")] };

    private static Observation Candidate(
        string type, long shallowBytes, long instances, double gen2Percent, long gen2Count,
        bool finalizable = false, bool staticRooted = false, bool container = false) =>
        RuleShapeGolden.Observation("leak.candidate-type", "Leak Candidate Analysis", new()
        {
            ["shallow.bytes"] = Bytes(shallowBytes),
            ["instance.count"] = Count(instances),
            ["gen2.percent"] = Percent(gen2Percent),
            ["gen2.count"] = Count(gen2Count),
            ["reference-field.ratio"] = Ratio(0.2),
            ["is-finalizable.flag"] = Count(finalizable ? 1 : 0),
            ["is-container.flag"] = Count(container ? 1 : 0),
            ["is-static-rooted.flag"] = Count(staticRooted ? 1 : 0),
            ["is-delegate.flag"] = Count(0),
        }) with { Subjects = [Type(type)] };

    private static Observation PinnedTarget(string type) =>
        RuleShapeGolden.Observation("handle.pinned-target-type", "GC Handle Analysis", new()) with { Subjects = [Type(type)] };

    private static Observation DependentTarget(string type) =>
        RuleShapeGolden.Observation("handle.dependent-target-type", "GC Handle Analysis", new()) with { Subjects = [Type(type)] };

    private static Observation BoxingPressure(long total, long enums, long nullables, long oversized, long gen2) =>
        RuleShapeGolden.Observation("type.boxing-pressure", "Boxing Analysis", new()
        {
            ["total-boxed.count"] = Count(total),
            ["total-boxed.bytes"] = Bytes(total * 24),
            ["boxed-enum.count"] = Count(enums),
            ["boxed-enum.bytes"] = Bytes(enums * 24),
            ["nullable-boxed.count"] = Count(nullables),
            ["nullable-boxed.bytes"] = Bytes(nullables * 24),
            ["oversized-value.count"] = Count(oversized),
            ["padding-waste-type.count"] = Count(1),
            ["gen2-boxed.count"] = Count(gen2),
        });

    private static Observation BoxedType(string type, bool enumType, bool equatable, long instances) =>
        RuleShapeGolden.Observation("type.boxing", "Boxing Analysis", new()
        {
            ["is-enum"] = Count(enumType ? 1 : 0),
            ["has-iequatable"] = Count(equatable ? 1 : 0),
            ["instance.count"] = Count(instances),
        }) with { Subjects = [Type(type)] };

    private static Observation PaddingWaste(string type, double ratio, long wasted, long structSize, long fieldBytes) =>
        RuleShapeGolden.Observation("type.struct-padding-waste", "Boxing Analysis", new()
        {
            ["waste.ratio"] = Ratio(ratio),
            ["wasted-padding.bytes"] = Count(wasted),
            ["struct-size.bytes"] = Count(structSize),
            ["field.bytes"] = Count(fieldBytes),
        }) with { Subjects = [Type(type)] };

    private static Observation OversizedType(string type, long staticSize, long instances) =>
        RuleShapeGolden.Observation("type.oversized-value", "Boxing Analysis", new()
        {
            ["static-size.bytes"] = Count(staticSize),
            ["instance.count"] = Count(instances),
        }) with { Subjects = [Type(type)] };

    private static Observation TaskPopulation(
        long total = 100, long pending = 0, long pendingOldGen = 0, long faulted = 0, bool cycle = false, long maxDepth = 1,
        long unresolvedTcsGen2 = 0, long pendingVtsGen2 = 0) =>
        RuleShapeGolden.Observation("async.task-population", "Async Task Analysis", new()
        {
            ["total.count"] = Count(total),
            ["pending.count"] = Count(pending),
            ["pending-oldgen.count"] = Count(pendingOldGen),
            ["faulted.count"] = Count(faulted),
            ["cycle-detected"] = Count(cycle ? 1 : 0),
            ["max-continuation-depth"] = Count(maxDepth),
            ["avg-continuation-depth"] = Count(2),
            ["total-tcs.count"] = Count(100),
            ["unresolved-tcs.count"] = Count(unresolvedTcsGen2),
            ["unresolved-tcs-oldgen.count"] = Count(unresolvedTcsGen2),
            ["total-vts.count"] = Count(50),
            ["pending-vts.count"] = Count(pendingVtsGen2),
            ["pending-vts-oldgen.count"] = Count(pendingVtsGen2),
        });

    private static Observation Orphan() =>
        RuleShapeGolden.Observation("type.orphaned-task", "Async Task Analysis", new());

    private static Observation HttpPopulation(long webResponses = 0, long handlers = 0) =>
        RuleShapeGolden.Observation("infra.http-object-population", "HTTP Object Analysis", new()
        {
            ["http-web-response.count"] = Count(webResponses),
            ["http-message-handler.count"] = Count(handlers),
        });

    private static IEnumerable<Observation> HttpObjects(string type, int count, int? generation = null, int? connectionLimit = null) =>
        Enumerable.Range(0, count).Select(_ =>
        {
            var measures = new Dictionary<string, Measure>();
            if (generation is int g) measures["generation.ordinal"] = Count(g);
            if (connectionLimit is int limit) measures["connection-limit"] = Count(limit);
            return RuleShapeGolden.Observation("infra.http-object", "HTTP Object Analysis", measures) with { Subjects = [Type(type)] };
        });

    private static Observation HandlerModule(string module, long instances) =>
        RuleShapeGolden.Observation("infra.http-handler-module", "HTTP Object Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [new ModuleRef { SimpleName = module, Fidelity = MatchFidelity.Exact }],
        };
}
