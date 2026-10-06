using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, cross-analyzer rules, fifth group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchFiveClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public async Task OrphanedTaskAccumulation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new OrphanedTaskAccumulationInsightRule(),
        [
            new("faulted-with-blocked-finalizer", [Tasks(total: 100, faulted: 4, orphaned: 5), FinalizerThread(blocked: true)]),
            new("high-orphan-ratio", [Tasks(100, 0, 60)]),
            new("both", [Tasks(100, 4, 60), FinalizerThread(true)]),
            new("faulted-but-finalizer-running-says-nothing", [Tasks(100, 4, 5), FinalizerThread(false)]),
        ]);
    }

    [Fact]
    public async Task OrphanedTaskAccumulation_CitesBothSourcesForTheFinalizerClaim()
    {
        Observation tasks = Tasks(100, 4, 5);
        Observation thread = FinalizerThread(true);

        Claim claim = (await RunAsync(new OrphanedTaskAccumulationInsightRule(), [tasks, thread])).Single();

        claim.Support.Should().HaveCount(2);
        claim.Support[0].Observations.Should().Equal(tasks.Id);
        claim.Support[1].Observations.Should().Equal(thread.Id);
    }

    [Fact]
    public async Task BoxingGCCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new BoxingGCCorrelationInsightRule(),
        [
            new("enums-and-gen0", [BoxingPressure(total: 50_000, enums: 20_000), Generations(total: 1_000, gen0: 500)]),
            new("enums-only", [BoxingPressure(50_000, 20_000), Generations(1_000, 100)]),
            new("gen0-only-no-generation-observation-for-enums", [BoxingPressure(50_000, 100), Generations(1_000, 500)]),
            new("neither-says-nothing", [BoxingPressure(50_000, 100), Generations(1_000, 100)]),
        ]);
    }

    [Fact]
    public async Task JitModuleHotspot_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new JitModuleHotspotInsightRule(),
        [
            new("large-on-disk", [JitFrames("Contoso.Core", 80), DiskFootprint("Contoso.Core", 40 * Mb)]),
            new("in-conflict", [JitFrames("Contoso.Core", 80), Conflict("Contoso.Core")]),
            new("both", [JitFrames("Contoso.Core", 80), DiskFootprint("Contoso.Core", 40 * Mb), Conflict("Contoso.Core")]),
            new("hot-but-uncorrelated-says-nothing", [JitFrames("Contoso.Core", 80)]),
            new("below-threshold-says-nothing", [JitFrames("Contoso.Core", 10), DiskFootprint("Contoso.Core", 40 * Mb)]),
        ]);
    }

    [Fact]
    public async Task JitModuleHotspot_SubjectIsTheModule_AndEachCorrelatedSourceIsCitedSeparately()
    {
        Observation frames = JitFrames("Contoso.Core", 80);
        Observation disk = DiskFootprint("Contoso.Core", 40 * Mb);
        Observation conflict = Conflict("Contoso.Core");

        Claim claim = (await RunAsync(new JitModuleHotspotInsightRule(), [frames, disk, conflict])).Single();

        claim.Assertion!.Subject.Should().BeOfType<ModuleRef>().Which.SimpleName.Should().Be("Contoso.Core");
        claim.Support.Should().HaveCount(3);
        claim.Support.SelectMany(s => s.Observations).Should().BeEquivalentTo([frames.Id, disk.Id, conflict.Id]);
    }

    [Fact]
    public async Task LongHeldTransaction_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LongHeldTransactionOnOpenConnectionInsightRule(),
        [
            new("warning", [.. LinkedPairs(count: 3)]),
            new("critical", [.. LinkedPairs(count: 15)]),
            new("below-threshold-says-nothing", [.. LinkedPairs(count: 2)]),
        ]);
    }

    [Fact]
    public async Task MemoryTypeGeneration_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new MemoryTypeGenerationInsightRule(),
        [
            new("info", [TypeProfile("App.Cache", total: 20 * Mb, gen2Bytes: 19 * Mb)]),
            new("warning-with-two-types", [TypeProfile("App.Cache", 200 * Mb, 190 * Mb), TypeProfile("App.Index", 20 * Mb, 19 * Mb)]),
            new("not-long-lived-says-nothing", [TypeProfile("App.Request", 200 * Mb, 20 * Mb)]),
            new("too-small-says-nothing", [TypeProfile("App.Tiny", Mb, Mb)]),
        ]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Observation Tasks(long total, long faulted, long orphaned) =>
        RuleShapeGolden.Observation("async.task-population", "Async Task Analysis", new()
        {
            ["total.count"] = Count(total),
            ["faulted.count"] = Count(faulted),
            ["orphaned.count"] = Count(orphaned),
        });

    private static Observation FinalizerThread(bool blocked) =>
        RuleShapeGolden.Observation("runtime.finalizer-thread", "Thread Analysis", new() { ["is-blocked"] = Count(blocked ? 1 : 0) });

    private static Observation BoxingPressure(long total, long enums) =>
        RuleShapeGolden.Observation("type.boxing-pressure", "Boxing Analysis", new()
        {
            ["total-boxed.count"] = Count(total),
            ["total-boxed.bytes"] = Bytes(total * 24),
            ["boxed-enum.count"] = Count(enums),
        });

    private static Observation Generations(long total, long gen0) =>
        RuleShapeGolden.Observation("gc.generation-composition", "GC Generation Analysis", new()
        {
            ["total.count"] = Count(total),
            ["gen0.count"] = Count(gen0),
        });

    private static ModuleRef Module(string name) => new() { SimpleName = name, Fidelity = MatchFidelity.Exact };

    private static Observation JitFrames(string module, long frames) =>
        RuleShapeGolden.Observation("runtime.jit-module-frames", "JIT Analysis", new() { ["active-frame.count"] = Count(frames) }) with
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

    private static IEnumerable<Observation> LinkedPairs(int count)
    {
        for (int i = 0; i < count; i++)
        {
            ObjectRef connection = new() { Address = (ulong)(0x1000 + i), Artifact = new ArtifactId("rule-shape-golden"), Fidelity = MatchFidelity.Exact };
            ObjectRef owner = new() { Address = (ulong)(0x9000 + i), Artifact = new ArtifactId("rule-shape-golden"), Fidelity = MatchFidelity.Exact };

            yield return RuleShapeGolden.Observation("infra.db-connection", "DB Connection Analysis", new()) with { Subjects = [owner, connection] };
            yield return RuleShapeGolden.Observation("infra.sql-transaction", "SQL Transaction Analysis", new()) with { Subjects = [owner, connection] };
        }
    }

    private static Observation TypeProfile(string type, long total, long gen2Bytes) =>
        RuleShapeGolden.Observation("gc.generation-type-profile", "GC Generation Analysis", new()
        {
            ["total.bytes"] = Bytes(total),
            ["gen2.bytes"] = Bytes(gen2Bytes),
            ["gen2.count"] = Count(100),
            ["gen0.count"] = Count(5),
            ["gen1.count"] = Count(5),
            ["loh.count"] = Count(0),
        }) with { Subjects = [new TypeRef { CanonicalName = type, Fidelity = MatchFidelity.Exact }] };
}
