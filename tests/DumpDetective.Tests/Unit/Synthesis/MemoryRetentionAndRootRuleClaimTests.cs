using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 4: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class MemoryRetentionAndRootRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── ObjectShape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ObjectShape_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ObjectShapeSynthesisRule(),
        [
            new("density-critical-by-scan-work", [Shape(avgRefFields: 6, scanWork: 300_000_000), RefHeavy("App.Order", 12, 40_000)]),
            new("density-warning", [Shape(9, 1_000_000), RefHeavy("App.Order", 12, 40_000)]),
            new("density-notice-no-heavy-types", [Shape(4.5, 1_000_000)]),
            new("value-heavy", [Shape(1, 10), ValueHeavy("App.Point", fields: 3, instances: 50_000)]),
            new("finalizable-reference-heavy", [Shape(1, 10), FinalizableHeavy("App.Handle", 12, 20_000), FinalizableHeavy("App.Pipe", 9, 15_000)]),
        ]);
    }

    [Theory]
    [InlineData(6.0, 300_000_000L, "critical GC scan-work threshold", 250_000_000.0)]
    [InlineData(9.0, 1_000_000L, "warning threshold", 8.0)]
    [InlineData(4.5, 1_000_000L, "notice threshold", 4.0)]
    public async Task ObjectShape_DensityReferencesTheThresholdThatSetItsSeverity(double avg, long scanWork, string basis, double budget)
    {
        IReadOnlyList<Claim> claims = await RunAsync(new ObjectShapeSynthesisRule(), [Shape(avg, scanWork)]);

        Claim claim = claims.Should().ContainSingle().Subject;
        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Exceeds);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
        claim.Assertion.Reference.Basis.Should().Be(basis);
    }

    // ── Memory ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Memory_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new MemorySynthesisRule(),
        [
            new("everything-fires", [Heap(lohPercent: 45, top1: 50, top5: 85, smallCount: 90, smallBytes: 50, objectsPerMb: 12_000, top1Name: "App.Cache")]),
            new("pressure-only", [Heap(39, 10, 79, 84, 44, 12_000)]),
            new("baseline", [Heap(5, 5, 20, 30, 10, 1_000)]),
        ]);
    }

    [Fact]
    public async Task Memory_BaselineClaimDescribesWithoutJudging_AndOthersCompareToTheirThreshold()
    {
        IReadOnlyList<Claim> baseline = await RunAsync(new MemorySynthesisRule(), [Heap(5, 5, 20, 30, 10, 1_000)]);
        IReadOnlyList<Claim> all = await RunAsync(new MemorySynthesisRule(), [Heap(45, 50, 85, 90, 50, 12_000, "App.Cache")]);

        baseline.Single().Assertion!.Predicate.Should().Be(AssertionPredicate.Composes);
        baseline.Single().Counter!.NoneFoundBecause.Should().Contain("nothing to weaken");

        all.Should().HaveCount(5);
        all.Single(c => c.Fingerprint.EndsWith("loh-usage")).Assertion!.Reference!.Value.Value.Should().Be(40);
        all.Single(c => c.Fingerprint.EndsWith("concentration")).Assertion!.Reference!.Value.Value.Should().Be(80);
        all.Single(c => c.Fingerprint.EndsWith("dominant-type")).Assertion!.Qualifiers.Location.Should().Contain("App.Cache");
        all.Single(c => c.Fingerprint.EndsWith("pressure-score")).Assertion!.Reference!.Value.Value.Should().Be(70);
    }

    // ── EventLeak ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EventLeak_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new EventLeakSynthesisRule(),
        [
            new("none", [LeakPopulation(total: 0, instanceGroups: 0, staticGroups: 0)]),
            new("instance-only", [LeakPopulation(10, 1, 0), LeakGroup(isStatic: false, "App.Hub", "Changed", instances: 4, subscribers: 40, severity: 22)]),
            new("static-critical-with-two-groups", [LeakPopulation(10, 0, 2), LeakGroup(true, "App.Bus", "Fired", 1, 300, 40), LeakGroup(true, "App.Log", "Written", 1, 20, 21)]),
        ]);
    }

    [Fact]
    public async Task EventLeak_CounterDependsOnWhetherThePublisherIsStatic()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new EventLeakSynthesisRule(),
            [LeakPopulation(10, 1, 1), LeakGroup(false, "App.Hub", "Changed", 4, 40, 22), LeakGroup(true, "App.Bus", "Fired", 1, 300, 40)]);

        Claim instance = claims.Single(c => c.Fingerprint.EndsWith("instance-retention"));
        Claim @static = claims.Single(c => c.Fingerprint.EndsWith("static-retention"));

        instance.Counter!.NoneFoundBecause.Should().Contain("only while the publisher itself is alive");
        @static.Counter!.NoneFoundBecause.Should().Contain("until it unsubscribes");
        instance.Assertion!.Measure!.Value.Value.Should().Be(40);
        @static.Assertion!.Measure!.Value.Value.Should().Be(300);
    }

    [Fact]
    public async Task EventLeak_AbsenceClaim_StatesItsOwnLimit()
    {
        Claim none = (await RunAsync(new EventLeakSynthesisRule(), [LeakPopulation(0, 0, 0)])).Single();

        none.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        none.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("thresholds");
    }

    // ── GCRoot ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GCRoot_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new GCRootSynthesisRule(),
        [
            new("static-warning-exact", [RootPopulation(), KindSummary(HeapRootKind.StrongHandle, count: 30, retained: 20_000_000, exact: true), TopRoot("App.Cache")]),
            new("static-critical-estimated", [RootPopulation(), KindSummary(HeapRootKind.StrongHandle, 30, 200_000_000, exact: false), TopRoot("App.Cache")]),
            new("finalizer-queue", [RootPopulation(), KindSummary(HeapRootKind.FinalizerQueue, 6_000, 0, true)]),
            new("pinned", [RootPopulation(), KindSummary(HeapRootKind.PinnedHandle, 300, 0, true), KindSummary(HeapRootKind.AsyncPinnedHandle, 250, 0, true)]),
            new("walk-capped", [RootPopulation(capped: 7)]),
        ]);
    }

    [Fact]
    public async Task GCRoot_StaticRetentionCounterFollowsWhetherTheSizeIsExact()
    {
        Claim exact = (await RunAsync(new GCRootSynthesisRule(), [RootPopulation(), KindSummary(HeapRootKind.StrongHandle, 30, 20_000_000, true)])).Single();
        Claim estimated = (await RunAsync(new GCRootSynthesisRule(), [RootPopulation(), KindSummary(HeapRootKind.StrongHandle, 30, 20_000_000, false)])).Single();

        exact.Counter!.Statements.Should().BeEmpty();
        estimated.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("estimate");
    }

    [Fact]
    public async Task GCRoot_WalkCapIsNoLongerAClaim_ItIsCoverage()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new GCRootSynthesisRule(), [RootPopulation(capped: 7)]);

        claims.Should().BeEmpty();
    }

    [Fact]
    public void GCRootCoverage_WalkCap_IsPartialWithAnUnknownTotal()
    {
        IReadOnlyList<CoverageRecord> records = new GCRootCoverageRule()
            .Evaluate(new ObservationMatchSet([RootPopulation(capped: 7)]), new SynthesisContext());

        CoverageRecord record = records.Should().ContainSingle().Subject;
        record.Capability.Key.Should().Be(CapabilityVocabulary.HeapReferences);
        record.Status.Should().Be(CoverageStatus.Partial);
        record.Boundary.Should().Be(new CoverageBoundary(7, null, "root-owned subgraphs", "were truncated at the node or depth budget"));
    }

    [Fact]
    public void GCRootCoverage_NoCap_ProducesNoRecord()
    {
        new GCRootCoverageRule().Evaluate(new ObservationMatchSet([RootPopulation()]), new SynthesisContext()).Should().BeEmpty();
    }

    // ── ReferenceChain ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReferenceChain_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ReferenceChainSynthesisRule(),
        [
            new("no-samples", [ChainPopulation(analyzed: 0, retained: 0, percent: 0, limited: 0)]),
            new("coverage-warning-with-limited", [ChainPopulation(10, 8, 80, limited: 3)]),
            new("coverage-info", [ChainPopulation(10, 2, 20, 0)]),
            new("finalizer-only-and-shared-root",
            [
                ChainPopulation(10, 5, 50, 0),
                TypeSample("App.Handle", instances: 500, samples: 5, retainedSamples: 5, dominantKind: "Finalizer", dominantCount: 5),
                SharedRoot(0xABC, "StaticVar", "App.A, App.B", typeCount: 2),
            ]),
        ]);
    }

    [Fact]
    public async Task ReferenceChain_CoveragePredicateAndCounterFollowTheEvidence()
    {
        Claim high = (await RunAsync(new ReferenceChainSynthesisRule(), [ChainPopulation(10, 8, 80, 3)])).Single(c => c.Fingerprint.EndsWith("retention-coverage"));
        Claim low = (await RunAsync(new ReferenceChainSynthesisRule(), [ChainPopulation(10, 2, 20, 0)])).Single();

        high.Assertion!.Predicate.Should().Be(AssertionPredicate.Retains);
        high.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("3");
        low.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        low.Counter!.NoneFoundBecause.Should().Contain("reachable");
    }

    [Fact]
    public async Task ReferenceChain_TraversalLimitIsNoLongerAClaim_ItIsCoverage()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new ReferenceChainSynthesisRule(), [ChainPopulation(10, 8, 80, 3)]);

        claims.Should().NotContain(c => c.Fingerprint.EndsWith("traversal-limit"));
    }

    [Fact]
    public void ReferenceChainCoverage_TraversalLimit_IsPartialWithTheBoundary()
    {
        IReadOnlyList<CoverageRecord> records = new ReferenceChainCoverageRule()
            .Evaluate(new ObservationMatchSet([ChainPopulation(10, 8, 80, 3)]), new SynthesisContext());

        CoverageRecord record = records.Should().ContainSingle().Subject;
        record.Capability.Key.Should().Be(CapabilityVocabulary.HeapRootPaths);
        record.Status.Should().Be(CoverageStatus.Partial);
        record.Boundary.Should().Be(new CoverageBoundary(3, 10, "sampled types", "hit the path or depth budget"));
        record.Impacts.Should().ContainSingle().Which.Should().Be(new CoverageImpact("Reference Chain Analysis", CoverageImpactKind.Limited));
    }

    [Fact]
    public void ReferenceChainCoverage_NoSamples_IsPartialWithAZeroBoundary()
    {
        IReadOnlyList<CoverageRecord> records = new ReferenceChainCoverageRule()
            .Evaluate(new ObservationMatchSet([ChainPopulation(0, 0, 0, 0)]), new SynthesisContext());

        records.Should().ContainSingle().Which.Boundary.Should().Be(
            new CoverageBoundary(0, 0, "sampled types", "no valid sample instance was found for the configured top types"));
    }

    [Fact]
    public void ReferenceChainCoverage_NoLimitHit_ProducesNoRecord()
    {
        new ReferenceChainCoverageRule()
            .Evaluate(new ObservationMatchSet([ChainPopulation(10, 2, 20, 0)]), new SynthesisContext())
            .Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static Observation Shape(double avgRefFields, long scanWork) =>
        RuleShapeGolden.Observation("type.shape-composition", "Object Shape Analysis", new()
        {
            ["type.count"] = Count(100),
            ["avg-reference-fields"] = new Measure(avgRefFields, MeasureUnit.Custom, MeasureSemantics.Ratio),
            ["gc-scan-work"] = Count(scanWork),
            ["reference-heavy-type.count"] = Count(1),
            ["value-heavy-type.count"] = Count(1),
        });

    private static Observation RefHeavy(string type, int fields, long instances) =>
        RuleShapeGolden.Observation("type.reference-heavy", "Object Shape Analysis", new()
        {
            ["reference-field.count"] = Count(fields),
            ["instance.count"] = Count(instances),
        }) with { Subjects = [Type(type)] };

    private static Observation ValueHeavy(string type, int fields, long instances) =>
        RuleShapeGolden.Observation("type.value-heavy", "Object Shape Analysis", new()
        {
            ["total-field.count"] = Count(fields),
            ["instance.count"] = Count(instances),
        }) with { Subjects = [Type(type)] };

    private static Observation FinalizableHeavy(string type, int fields, long instances) =>
        RuleShapeGolden.Observation("type.finalizable-reference-heavy", "Object Shape Analysis", new()
        {
            ["reference-field.count"] = Count(fields),
            ["instance.count"] = Count(instances),
        }) with { Subjects = [Type(type)] };

    private static Observation Heap(
        double lohPercent, double top1, double top5, double smallCount, double smallBytes, double objectsPerMb, string? top1Name = null) =>
        RuleShapeGolden.Observation("heap.memory-composition", "Memory Analysis", new()
        {
            ["total.bytes"] = Bytes(800 * Mb),
            ["loh.bytes"] = Bytes(200 * Mb),
            ["loh.percent"] = Percent(lohPercent),
            ["loh.count"] = Count(40),
            ["unique-types.count"] = Count(900),
            ["top1.percent"] = Percent(top1),
            ["top5.percent"] = Percent(top5),
            ["small-object.count-percent"] = Percent(smallCount),
            ["small-object.bytes-percent"] = Percent(smallBytes),
            ["objects-per-mb"] = Count(objectsPerMb),
            ["top1-type.bytes"] = Bytes(400 * Mb),
        }) with { Evidence = top1Name is null ? [] : [Describe(top1Name)] };

    private static Observation LeakPopulation(long total, long instanceGroups, long staticGroups) =>
        RuleShapeGolden.Observation("event.publisher-leak-population", "Event Leak Analysis", new()
        {
            ["total.count"] = Count(total),
            ["instance-leak.count"] = Count(instanceGroups),
            ["static-leak.count"] = Count(staticGroups),
            ["top-instance-publisher.has-root-path"] = Count(1),
            ["top-instance-publisher.is-root-path-search-truncated"] = Count(0),
            ["top-instance-publisher.evidence-signal.count"] = Count(1),
            ["top-static-publisher.has-root-path"] = Count(1),
            ["top-static-publisher.is-root-path-search-truncated"] = Count(0),
            ["top-static-publisher.evidence-signal.count"] = Count(1),
        });

    private static Observation LeakGroup(bool isStatic, string publisher, string field, long instances, long subscribers, long severity) =>
        RuleShapeGolden.Observation("event.publisher-leak-group", "Event Leak Analysis", new()
        {
            ["is-static"] = Count(isStatic ? 1 : 0),
            ["instance.count"] = Count(instances),
            ["subscriber.count"] = Count(subscribers),
            ["max-subscribers-per-instance.count"] = Count(subscribers),
            ["max-severity.score"] = Count(severity),
            ["is-timer-event"] = Count(0),
            ["is-property-changed-event"] = Count(0),
        }) with { Subjects = [Type(publisher)], Evidence = [Describe(field)] };

    private static Observation RootPopulation(int capped = 0) =>
        RuleShapeGolden.Observation("root.population", "GC Root Analysis", new()
        {
            ["total.count"] = Count(500),
            ["subgraph-walk-capped"] = Count(capped > 0 ? 1 : 0),
            ["subgraph-walk-capped.count"] = Count(capped),
        });

    private static Observation KindSummary(HeapRootKind kind, int count, long retained, bool exact) =>
        RuleShapeGolden.Observation("root.kind-summary", "GC Root Analysis", new()
        {
            ["kind.ordinal"] = Count((int)kind),
            ["count"] = Count(count),
            ["retained.bytes"] = Bytes(retained),
            ["is-retained-exact"] = Count(exact ? 1 : 0),
        });

    private static Observation TopRoot(string type) =>
        RuleShapeGolden.Observation("heap.root", "GC Root Analysis", new()) with { Subjects = [Type(type)] };

    private static Observation ChainPopulation(long analyzed, long retained, double percent, long limited) =>
        RuleShapeGolden.Observation("reference-chain.population", "Reference Chain Analysis", new()
        {
            ["analyzed.count"] = Count(analyzed),
            ["retained.count"] = Count(retained),
            ["retained.percent"] = Percent(percent),
            ["traversal-limited.count"] = Count(limited),
        });

    private static Observation TypeSample(
        string type, long instances, long samples, long retainedSamples, string dominantKind, long dominantCount) =>
        RuleShapeGolden.Observation("reference-chain.type-sample", "Reference Chain Analysis", new()
        {
            ["instance.count"] = Count(instances),
            ["sample.count"] = Count(samples),
            ["retained-sample.count"] = Count(retainedSamples),
            ["dominant-root-kind.count"] = Count(dominantCount),
        }) with { Subjects = [Type(type)], Evidence = [Describe(dominantKind)] };

    private static Observation SharedRoot(ulong address, string rootKind, string typeNames, long typeCount) =>
        RuleShapeGolden.Observation("reference-chain.shared-root-group", "Reference Chain Analysis", new() { ["type.count"] = Count(typeCount) }) with
        {
            Subjects = [new ObjectRef { Address = address, Artifact = new ArtifactId("rule-shape-golden"), Fidelity = MatchFidelity.Exact }],
            Evidence = [Describe(rootKind), Describe(typeNames)],
        };
}
