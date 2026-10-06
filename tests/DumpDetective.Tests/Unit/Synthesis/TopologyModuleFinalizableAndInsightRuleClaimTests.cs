using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 8: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class TopologyModuleFinalizableAndInsightRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── HeapTopology ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HeapTopology_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new HeapTopologySynthesisRule(),
        [
            new("quiet", [Kind(0, committed: 800 * Mb, segments: 10), Kind(1, 100 * Mb, 2), TopologyPopulation()]),
            new("everything-fires",
            [
                Kind(0, committed: 400 * Mb, segments: 10, fragmented: 200 * Mb),
                Kind(1, 450 * Mb, 4, fragmented: 300 * Mb),
                Kind(2, 100 * Mb, 2, fragmented: 30 * Mb),
                Kind(3, 50 * Mb, 1, fragmented: 20 * Mb),
                Kind(4, 5 * Mb, 2),
                TopologyPopulation(logicalHeaps: 4, max: 400 * Mb, min: 100 * Mb, serverGc: true),
            ]),
            new("lohwarning-workstation-skew", [Kind(0, 700 * Mb, 10), Kind(1, 300 * Mb, 2), TopologyPopulation(logicalHeaps: 2, max: 500 * Mb, min: 100 * Mb, serverGc: false)]),
        ]);
    }

    [Theory]
    [InlineData(450, 400, "critical LOH-share threshold", AssertionPredicate.Exceeds)]
    [InlineData(300, 700, "warning LOH-share threshold", AssertionPredicate.Exceeds)]
    public async Task HeapTopology_SegmentDistributionReferencesTheShareThresholdItCrossed(long lohMb, long sohMb, string basis, AssertionPredicate predicate)
    {
        Claim claim = (await RunAsync(new HeapTopologySynthesisRule(), [Kind(0, sohMb * Mb, 10), Kind(1, lohMb * Mb, 4), TopologyPopulation()]))
            .Single(c => c.Fingerprint.EndsWith("segment-distribution"));

        claim.Assertion!.Predicate.Should().Be(predicate);
        claim.Assertion.Reference!.Basis.Should().Be(basis);
    }

    [Fact]
    public async Task HeapTopology_PohAndFrozenFragmentationAdmitTheirOwnBenignExplanation()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new HeapTopologySynthesisRule(),
            [Kind(0, 400 * Mb, 10), Kind(2, 100 * Mb, 2, fragmented: 30 * Mb), Kind(3, 50 * Mb, 1, fragmented: 20 * Mb), TopologyPopulation()]);

        claims.Single(c => c.Fingerprint.EndsWith("poh-fragmentation")).Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("typical");
        claims.Single(c => c.Fingerprint.EndsWith("frozen-fragmentation")).Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("never collected");
    }

    // ── Module ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Module_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ModuleSynthesisRule(),
        [
            new("quiet-snapshot", [ModulePopulation()]),
            new("snapshot-with-unidentifiable-duplicates", [ModulePopulation(unknownDuplicates: 2), UnknownIdentity("Contoso.Dup"), UnknownIdentity("Contoso.Dup2")]),
            new("everything-fires",
            [
                ModulePopulation(conflicts: 3, dynamicTotal: 120, anonymous: 8, domains: 3, unknownDuplicates: 2),
                ConflictGroup("Newtonsoft.Json", "12.0 vs 13.0"),
                Footprint("Contoso.Data", 300 * Mb, 4_000_000, 120),
                Density("Contoso.Buffers", types: 3, bytes: 200 * Mb, perType: 66 * Mb),
                CrossDomain("Contoso.Shared", domains: 4, bytes: 10 * Mb),
                AssemblyRefMismatch("Contoso.App", "Newtonsoft.Json 13.0"),
                UnknownIdentity("Contoso.Dup"),
            ]),
            new("warning-conflict-and-light-heap", [ModulePopulation(conflicts: 1), ConflictGroup("Serilog", "2.0 vs 3.0"), Footprint("Contoso.Data", Mb, 100, 5)]),
        ]);
    }

    [Fact]
    public async Task Module_ConflictClaimFollowsWhetherAnyConflictWasFound()
    {
        Claim none = (await RunAsync(new ModuleSynthesisRule(), [ModulePopulation()])).Single();
        Claim some = (await RunAsync(new ModuleSynthesisRule(), [ModulePopulation(conflicts: 1), ConflictGroup("Serilog", "2.0 vs 3.0")])).Single();
        Claim hidden = (await RunAsync(new ModuleSynthesisRule(), [ModulePopulation(unknownDuplicates: 2), UnknownIdentity("A"), UnknownIdentity("B")]))
            .Single(c => c.Fingerprint.EndsWith("version-conflicts"));

        none.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        some.Assertion!.Predicate.Should().Be(AssertionPredicate.Conflicts);
        hidden.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("could not be detected");
    }

    [Fact]
    public async Task Module_ClaimsCiteOnlyObservationsInTheirOwnLineage_IncludingTheEmptyUnknownIdentityCase()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new ModuleSynthesisRule(), [ModulePopulation(unknownDuplicates: 2)]);

        Claim unknown = claims.Single(c => c.Fingerprint.EndsWith("unknown-identity-duplicates"));
        unknown.Assertion!.Predicate.Should().Be(AssertionPredicate.Missing);
        unknown.Support.Should().ContainSingle().Which.Observations.Should().OnlyContain(id => unknown.DerivedFrom.Contains(id));
    }

    // ── FinalizableObject ────────────────────────────────────────────────────

    [Fact]
    public async Task FinalizableObject_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new FinalizableObjectSynthesisRule(),
        [
            new("gen2-accumulation", [FinalizablePopulation("App.Handle", instances: 20_000, gen2: 12_000, bytes: 40 * Mb)]),
            new("queue-backlog-critical", [QueuePopulation(20_000)]),
            new("retained-bytes-exact", [QueueEntry("App.Cache", retained: 150 * Mb, exact: true)]),
            new("retained-bytes-estimated-and-undisposed", [QueueEntry("App.Cache", 20 * Mb, exact: false, disposedButLive: true)]),
            new("critical-finalizers", [.. Enumerable.Range(0, 150).Select(_ => QueueEntry("Microsoft.Win32.SafeHandles.SafeFileHandle", Mb / 10, exact: true, critical: true))]),
            new("known-queue-patterns",
            [
                .. Enumerable.Range(0, 12).Select(_ => QueueEntry("System.Reflection.Emit.DynamicResolver", 1_000, true)),
                .. Enumerable.Range(0, 6).Select(_ => QueueEntry("System.Threading.Thread", 1_000, true)),
                .. Enumerable.Range(0, 7).Select(_ => QueueEntry("System.Threading.TimerHolder", 1_000, true)),
                .. Enumerable.Range(0, 4).Select(_ => QueueEntry("System.Threading.ReaderWriterLock", 1_000, true)),
            ]),
        ]);
    }

    [Fact]
    public async Task FinalizableObject_RetainedBytesCounterOwnsUpToAPartialEstimate()
    {
        Claim exact = (await RunAsync(new FinalizableObjectSynthesisRule(), [QueueEntry("App.Cache", 150 * Mb, exact: true)])).Single(c => c.Fingerprint.EndsWith("queue-retained-bytes"));
        Claim estimated = (await RunAsync(new FinalizableObjectSynthesisRule(), [QueueEntry("App.Cache", 150 * Mb, exact: false)])).Single(c => c.Fingerprint.EndsWith("queue-retained-bytes"));

        exact.Counter!.Statements.Should().BeEmpty();
        estimated.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("underestimates");
    }

    [Fact]
    public async Task FinalizableObject_QueuePatternClaimsReferenceTheThresholdTheyCrossed()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new FinalizableObjectSynthesisRule(),
            [.. Enumerable.Range(0, 60).Select(_ => QueueEntry("System.Threading.Thread", 1_000, true)), .. Enumerable.Range(0, 7).Select(_ => QueueEntry("System.Threading.TimerHolder", 1_000, true))]);

        claims.Single(c => c.Fingerprint.EndsWith("abandoned-thread-objects")).Assertion!.Reference!.Basis.Should().Be("warning threshold");
        claims.Single(c => c.Fingerprint.EndsWith("undisposed-timer-instances")).Assertion!.Reference!.Basis.Should().Be("notice threshold");
    }

    // ── PohGrowth (Insight) ──────────────────────────────────────────────────

    [Fact]
    public async Task PohGrowthInsight_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new PohGrowthInsightRule(),
        [
            new("below-threshold-says-nothing", [PohPopulation(percent: 5)]),
            new("above-threshold", [PohPopulation(percent: 15)]),
        ]);
    }

    // ── WastefulCollections (Insight) ────────────────────────────────────────

    [Fact]
    public async Task WastefulCollectionsInsight_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new WastefulCollectionsInsightRule(),
        [
            new("no-waste-says-nothing", [WasteSummary(wasteful: 0, wasted: 0)]),
            new("some-waste", [WasteSummary(wasteful: 12, wasted: 60 * Mb)]),
        ]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static ModuleRef Module(string name) => new() { SimpleName = name, Fidelity = MatchFidelity.Exact };

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation Kind(int ordinal, long committed, int segments, long fragmented = 0) =>
        RuleShapeGolden.Observation("heap.segment-kind-composition", "Heap Topology", new()
        {
            ["kind.ordinal"] = Count(ordinal),
            ["committed.bytes"] = Bytes(committed),
            ["segment.count"] = Count(segments),
            ["fragmented.bytes"] = Bytes(fragmented),
        });

    private static Observation TopologyPopulation(long logicalHeaps = 1, long max = 0, long min = 0, bool serverGc = false) =>
        RuleShapeGolden.Observation("heap.topology-population", "Heap Topology", new()
        {
            ["logical-heap.count"] = Count(logicalHeaps),
            ["logical-heap.max-bytes"] = Bytes(max),
            ["logical-heap.min-bytes"] = Bytes(min),
            ["is-server-gc"] = Count(serverGc ? 1 : 0),
        });

    private static Observation ModulePopulation(
        long conflicts = 0, long dynamicTotal = 0, long anonymous = 0, long domains = 1, long unknownDuplicates = 0) =>
        RuleShapeGolden.Observation("runtime.module-population", "Module Analysis", new()
        {
            ["total.count"] = Count(200),
            ["dynamic.count"] = Count(dynamicTotal),
            ["version-conflict.count"] = Count(conflicts),
            ["heavy-module-warning.threshold-bytes"] = Bytes(100 * Mb),
            ["total-dynamic.count"] = Count(dynamicTotal),
            ["total-domains.count"] = Count(domains),
            ["anonymous-module.count"] = Count(anonymous),
            ["unknown-identity-duplicate.count"] = Count(unknownDuplicates),
        });

    private static Observation ConflictGroup(string module, string versions) =>
        RuleShapeGolden.Observation("runtime.module-conflict-group", "Module Analysis", new()) with
        {
            Subjects = [Module(module)],
            Evidence = [Describe(versions)],
        };

    private static Observation Footprint(string module, long bytes, long objects, long types) =>
        RuleShapeGolden.Observation("runtime.module-heap-footprint", "Module Analysis", new()
        {
            ["shallow.bytes"] = Bytes(bytes),
            ["object.count"] = Count(objects),
            ["unique-type.count"] = Count(types),
        }) with { Subjects = [Module(module)] };

    private static Observation Density(string module, long types, long bytes, long perType) =>
        RuleShapeGolden.Observation("runtime.module-type-density", "Module Analysis", new()
        {
            ["unique-type.count"] = Count(types),
            ["shallow.bytes"] = Bytes(bytes),
            ["bytes-per-type.bytes"] = Bytes(perType),
        }) with { Subjects = [Module(module)] };

    private static Observation CrossDomain(string module, long domains, long bytes) =>
        RuleShapeGolden.Observation("runtime.module-cross-domain-load", "Module Analysis", new()
        {
            ["domain.count"] = Count(domains),
            ["shallow.bytes"] = Bytes(bytes),
        }) with { Subjects = [Module(module)] };

    private static Observation AssemblyRefMismatch(string module, string detail) =>
        RuleShapeGolden.Observation("runtime.module-assemblyref-mismatch", "Module Analysis", new()) with
        {
            Subjects = [Module(module)],
            Evidence = [Describe(detail)],
        };

    private static Observation UnknownIdentity(string module) =>
        RuleShapeGolden.Observation("runtime.module-unknown-identity", "Module Analysis", new()) with { Subjects = [Module(module)] };

    private static Observation FinalizablePopulation(string type, long instances, long gen2, long bytes) =>
        RuleShapeGolden.Observation("type.finalizable-population", "Finalizable Object Analysis", new()
        {
            ["instance.count"] = Count(instances),
            ["shallow.bytes"] = Bytes(bytes),
            ["gen2.count"] = Count(gen2),
        }) with { Subjects = [Type(type)] };

    private static Observation QueuePopulation(long queueCount) =>
        RuleShapeGolden.Observation("finalizer.queue-population", "Finalizable Object Analysis", new() { ["queue.count"] = Count(queueCount) });

    private static Observation QueueEntry(string type, long retained, bool exact, bool disposedButLive = false, bool critical = false) =>
        RuleShapeGolden.Observation("finalizer.queue-entry", "Finalizable Object Analysis", new()
        {
            ["retained.bytes"] = Bytes(retained),
            ["is-retained-exact"] = Count(exact ? 1 : 0),
            ["disposed-but-live"] = Count(disposedButLive ? 1 : 0),
            ["is-critical-finalizer"] = Count(critical ? 1 : 0),
            ["shallow.bytes"] = Bytes(retained / 2),
        }) with { Subjects = [Type(type)] };

    private static Observation PohPopulation(double percent) =>
        RuleShapeGolden.Observation("heap.topology-population", "Heap Topology", new()
        {
            ["poh.percent"] = Percent(percent),
            ["poh.bytes"] = Bytes(80 * Mb),
        });

    private static Observation WasteSummary(long wasteful, long wasted) =>
        RuleShapeGolden.Observation("collection.waste-summary", "Collection Analysis", new()
        {
            ["wasteful-collection.count"] = Count(wasteful),
            ["total-wasted.bytes"] = Bytes(wasted),
            ["total-collection.count"] = Count(1_000),
        });
}
