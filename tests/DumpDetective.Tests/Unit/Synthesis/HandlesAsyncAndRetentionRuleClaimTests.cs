using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 6: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class HandlesAsyncAndRetentionRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── GCHandle ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GCHandle_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new GCHandleSynthesisRule(),
        [
            new("quiet-summary-only", [Handles()]),
            new("summary-warning-by-pinned", [Handles(pinnedTargets: 600)]),
            new("summary-warning-by-total", [Handles(total: 20_000)]),
            new("everything-fires",
            [
                Handles(total: 20_000, pinnedTargets: 600, pinnedRetained: 200 * Mb, pinnedSoh: 80, asyncPinnedSoh: 30, refCounted: 600,
                        weakLongGen2: 900, weakLongLoh: 100, weakLongGen0: 100, dependent: 100, dependentUnresolved: 60),
                RefCountedTarget("App.ComWrapper", 300),
                RefCountedTarget("App.Other", 100),
            ]),
        ]);
    }

    [Theory]
    [InlineData(600, 100, "pinned-target warning threshold", 500.0)]
    [InlineData(10, 20_000, "total-handle warning threshold", 10_000.0)]
    public async Task GCHandle_SummaryReferencesTheThresholdThatMadeItAWarning(long pinned, long total, string basis, double budget)
    {
        Claim summary = (await RunAsync(new GCHandleSynthesisRule(), [Handles(total: total, pinnedTargets: pinned)])).Single();

        summary.Assertion!.Predicate.Should().Be(AssertionPredicate.Exceeds);
        summary.Assertion.Reference!.Basis.Should().Be(basis);
        summary.Assertion.Reference.Value.Value.Should().Be(budget);
    }

    [Fact]
    public async Task GCHandle_QuietSummaryDescribesWithoutJudging()
    {
        Claim summary = (await RunAsync(new GCHandleSynthesisRule(), [Handles()])).Single();

        summary.Assertion!.Predicate.Should().Be(AssertionPredicate.Composes);
        summary.Counter!.NoneFoundBecause.Should().Contain("nothing to weaken");
    }

    // ── WcfChannel ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WcfChannel_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new WcfChannelSynthesisRule(),
        [
            new("one-factory-only", [WcfPopulation(total: 10, factories: 1)]),
            new("many-factories-and-warning-count", [WcfPopulation(150, factories: 6, opened: 100, closed: 20)]),
            new("faulted-and-critical-count-mostly-closed", [WcfPopulation(600, factories: 0, opened: 50, closed: 400), WcfChannel("System.ServiceModel.Channels.ServiceChannel", "net.tcp://host/svc"), WcfChannel("System.ServiceModel.Channels.ServiceChannel", "net.tcp://host/svc"), WcfTypePopulation("System.ServiceModel.Channels.ServiceChannel")]),
        ]);
    }

    [Fact]
    public async Task WcfChannel_FactoryCounterArguesFromHowFewFactoriesExist()
    {
        Claim few = (await RunAsync(new WcfChannelSynthesisRule(), [WcfPopulation(10, factories: 1)])).Single();
        Claim many = (await RunAsync(new WcfChannelSynthesisRule(), [WcfPopulation(10, factories: 6)])).Single();

        few.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("recommended pattern");
        many.Counter!.Statements.Should().BeEmpty();
        many.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WcfChannel_FaultedClaimCitesEachFaultedChannelAndNamesTheFaultsAsItsPredicate()
    {
        Observation channel = WcfChannel("System.ServiceModel.Channels.ServiceChannel", "net.tcp://host/svc");
        IReadOnlyList<Claim> claims = await RunAsync(new WcfChannelSynthesisRule(), [WcfPopulation(10, 0), channel, WcfTypePopulation("System.ServiceModel.Channels.ServiceChannel")]);

        Claim faulted = claims.Single(c => c.Fingerprint.EndsWith("faulted-channels"));
        faulted.Assertion!.Predicate.Should().Be(AssertionPredicate.Faults);
        faulted.Support[0].Observations.Should().Equal(channel.Id);
    }

    // ── AsyncStateMachine ────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncStateMachine_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AsyncStateMachineSynthesisRule(),
        [
            new("high-count-and-fire-and-forget", [StateMachine("App.Worker", "RunAsync", instances: 1_500, bytes: 3 * Mb)]),
            new("critical-count-and-async-void", [StateMachine("App.Handlers", "OnClick", 12_000, 30 * Mb, asyncVoid: true)]),
            new("large-captures", [Closure("App.Context", 60 * Mb), Closure("App.Cache", 10 * Mb)]),
            new("large-captures-critical", [Closure("App.Context", 250 * Mb)]),
        ]);
    }

    [Fact]
    public async Task AsyncStateMachine_FireAndForgetClaimsAreOnePerMethod_EachCitingItsOwnStateMachines()
    {
        Observation first = StateMachine("App.A", "RunAsync", 200, Mb);
        Observation second = StateMachine("App.B", "PumpAsync", 150, Mb);

        IReadOnlyList<Claim> claims = await RunAsync(new AsyncStateMachineSynthesisRule(), [first, second]);

        Claim[] fireAndForget = claims.Where(c => c.Fingerprint.Contains("fire-and-forget")).ToArray();
        fireAndForget.Should().HaveCount(2);
        fireAndForget[0].Support.Should().ContainSingle().Which.Observations.Should().Equal(first.Id);
        fireAndForget[1].Support.Should().ContainSingle().Which.Observations.Should().Equal(second.Id);
        fireAndForget.Should().OnlyContain(c => c.Assertion!.Predicate == AssertionPredicate.Leaks);
    }

    [Fact]
    public async Task AsyncStateMachine_AsyncVoidNamesItsMethodAndAllowsEventHandlers()
    {
        Claim claim = (await RunAsync(new AsyncStateMachineSynthesisRule(), [StateMachine("App.Handlers", "OnClick", 12, Mb, asyncVoid: true)]))
            .Single(c => c.Fingerprint.EndsWith("async-void-method"));

        claim.Assertion!.Qualifiers.Location.Should().Contain("OnClick");
        claim.Counter!.NoneFoundBecause.Should().Contain("event handlers");
    }

    // ── WeakReference ────────────────────────────────────────────────────────

    [Fact]
    public async Task WeakReference_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new WeakReferenceSynthesisRule(),
        [
            new("quiet-overview-only", [WeakPopulation(total: 100, dead: 10)]),
            new("high-ratio-warning", [WeakPopulation(100, 60), StaleWrapper(), StaleWrapper()]),
            new("high-ratio-critical-and-high-count", [WeakPopulation(200_000, 150_000)]),
            new("dead-keys-and-held-only", [WeakPopulation(100, 10, deadKeys: 4, heldOnlyAvailable: true), WeakTarget("App.Item", heldOnly: true), WeakTarget("App.Item", heldOnly: true), WeakTarget("App.Other", heldOnly: false)]),
        ]);
    }

    [Fact]
    public async Task WeakReference_RatioClaimReferencesTheThresholdItCrossed()
    {
        Claim warning = (await RunAsync(new WeakReferenceSynthesisRule(), [WeakPopulation(100, 60)])).Single(c => c.Fingerprint.EndsWith("high-dead-ratio"));
        Claim critical = (await RunAsync(new WeakReferenceSynthesisRule(), [WeakPopulation(100, 85)])).Single(c => c.Fingerprint.EndsWith("high-dead-ratio"));

        warning.Assertion!.Reference!.Value.Value.Should().Be(50);
        critical.Assertion!.Reference!.Value.Value.Should().Be(80);
        critical.Assertion.Measure!.Value.Value.Should().BeApproximately(85, 0.001);
    }

    [Fact]
    public async Task WeakReference_OverviewDescribes_AndHeldOnlyClaimAssertsNoLeak()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new WeakReferenceSynthesisRule(), [WeakPopulation(100, 10, heldOnlyAvailable: true), WeakTarget("App.Item", true)]);

        claims.Single(c => c.Fingerprint.EndsWith("overview")).Assertion!.Predicate.Should().Be(AssertionPredicate.Composes);
        claims.Single(c => c.Fingerprint.EndsWith("held-only-via-weak-reference")).Counter!.NoneFoundBecause.Should().Contain("no leak");
    }

    // ── Dominator ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dominator_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new DominatorSynthesisRule(),
        [
            new("critical-retention-with-complete-counting", [Retention("App.Cache", 600 * Mb, 5_000), DomPopulation(highlyReferenced: 12)]),
            new("warning-retention-with-bounded-counting", [Retention("App.Cache", 150 * Mb, 5_000), DomPopulation(highlyReferenced: 3, approximated: 40, scanCapped: true), Hotspot("App.Hub", 12_000)]),
            new("info-retention-hotspot-warning", [Retention("App.Small", 10 * Mb, 100), DomPopulation(0), Hotspot("App.Hub", 2_000)]),
            new("reference-tracking-capped", [DomPopulation(0, approximated: 25)]),
        ]);
    }

    [Theory]
    [InlineData(600, "critical threshold")]
    [InlineData(150, "warning threshold")]
    public async Task Dominator_TopSuspectReferencesTheThresholdItCrossed_AndNoneBelowThem(long retainedMb, string basis)
    {
        Claim claim = (await RunAsync(new DominatorSynthesisRule(), [Retention("App.Cache", retainedMb * Mb, 10)])).Single();

        claim.Assertion!.Reference!.Basis.Should().Be(basis);
        claim.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("estimate");
    }

    [Fact]
    public async Task Dominator_BelowTheWarningSizeThereIsNoReference()
    {
        Claim claim = (await RunAsync(new DominatorSynthesisRule(), [Retention("App.Small", 10 * Mb, 10)])).Single();

        claim.Assertion!.Reference.Should().BeNull();
    }

    [Fact]
    public async Task Dominator_ReferenceCountingCounterIsEvidenceOnlyWhenCountingWasBounded()
    {
        IReadOnlyList<Claim> bounded = await RunAsync(new DominatorSynthesisRule(), [DomPopulation(highlyReferenced: 3, scanCapped: true)]);
        IReadOnlyList<Claim> complete = await RunAsync(new DominatorSynthesisRule(), [DomPopulation(highlyReferenced: 3)]);

        bounded.Single(c => c.Fingerprint.EndsWith("highly-referenced-objects")).Counter!.Statements.Should().ContainSingle();
        complete.Single(c => c.Fingerprint.EndsWith("highly-referenced-objects")).Counter!.Statements.Should().BeEmpty();
    }

    [Fact]
    public async Task Dominator_ApproximatedReferenceCountsAreNoLongerAClaim_TheyAreCoverage()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new DominatorSynthesisRule(), [DomPopulation(highlyReferenced: 0, approximated: 40)]);

        claims.Should().NotContain(c => c.Fingerprint.EndsWith("reference-tracking-capped"));
    }

    [Fact]
    public void DominatorCoverage_ApproximatedCounts_IsPartialWithAnUnknownTotal()
    {
        IReadOnlyList<CoverageRecord> records = new DominatorCoverageRule()
            .Evaluate(new ObservationMatchSet([DomPopulation(highlyReferenced: 0, approximated: 40)]), new SynthesisContext());

        CoverageRecord record = records.Should().ContainSingle().Subject;
        record.Capability.Key.Should().Be(CapabilityVocabulary.HeapReverseReferences);
        record.Status.Should().Be(CoverageStatus.Partial);
        record.Boundary.Should().Be(new CoverageBoundary(40, null, "addresses", "have approximated incoming-reference counts past the tracking limit"));
    }

    [Fact]
    public void DominatorCoverage_ExactCounts_ProducesNoRecord()
    {
        new DominatorCoverageRule().Evaluate(new ObservationMatchSet([DomPopulation(highlyReferenced: 3)]), new SynthesisContext()).Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation Handles(
        long total = 100, long pinnedTargets = 10, long pinnedRetained = 0, long pinnedSoh = 0, long asyncPinnedSoh = 0, long refCounted = 0,
        long weakLongGen2 = 0, long weakLongLoh = 0, long weakLongGen0 = 0, long dependent = 0, long dependentUnresolved = 0) =>
        RuleShapeGolden.Observation("handle.population", "GC Handle Analysis", new()
        {
            ["total.count"] = Count(total),
            ["pinned-target.count"] = Count(pinnedTargets),
            ["pinned-retained.bytes"] = Bytes(pinnedRetained),
            ["pinned-soh.count"] = Count(pinnedSoh),
            ["async-pinned-soh.count"] = Count(asyncPinnedSoh),
            ["pinned-non-soh.count"] = Count(0),
            ["async-pinned-non-soh.count"] = Count(0),
            ["refcounted.count"] = Count(refCounted),
            ["weakshort-gen0.count"] = Count(10),
            ["weakshort-gen1.count"] = Count(5),
            ["weakshort-gen2.count"] = Count(5),
            ["weakshort-loh.count"] = Count(0),
            ["weaklong-gen0.count"] = Count(weakLongGen0),
            ["weaklong-gen1.count"] = Count(0),
            ["weaklong-gen2.count"] = Count(weakLongGen2),
            ["weaklong-loh.count"] = Count(weakLongLoh),
            ["dependent.count"] = Count(dependent),
            ["dependent-resolved-edge.count"] = Count(dependent - dependentUnresolved),
            ["dependent-unresolved-target.count"] = Count(dependentUnresolved),
            ["total-handles-warning.threshold"] = Count(10_000),
            ["pinned-handle-targets-warning.threshold"] = Count(500),
            ["pinned-retained-bytes-warning.threshold"] = Bytes(100 * Mb),
            ["pinned-soh-warning.threshold"] = Count(100),
            ["refcounted-warning.threshold"] = Count(500),
            ["weaklong-gen2-fraction-warning.threshold"] = Percent(80),
            ["weaklong-gen2-minimum-count.threshold"] = Count(500),
            ["dependent-unresolved-percent-warning.threshold"] = Percent(50),
        });

    private static Observation RefCountedTarget(string type, long instances) =>
        RuleShapeGolden.Observation("handle.refcounted-target", "GC Handle Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation WcfPopulation(long total, long factories, long opened = 0, long closed = 0) =>
        RuleShapeGolden.Observation("infra.wcf-channel-population", "WCF Channel Analysis", new()
        {
            ["total.count"] = Count(total),
            ["opening.count"] = Count(0),
            ["opened.count"] = Count(opened),
            ["closing.count"] = Count(0),
            ["closed.count"] = Count(closed),
            ["other.count"] = Count(0),
            ["invalid-state.count"] = Count(0),
            ["duplex.count"] = Count(0),
            ["session.count"] = Count(0),
            ["factory.count"] = Count(factories),
        });

    private static Observation WcfChannel(string type, string remote) =>
        RuleShapeGolden.Observation("infra.wcf-channel", "WCF Channel Analysis", new() { ["binding-hint.ordinal"] = Count(0) }) with
        {
            Subjects = [Type(type)],
            Evidence = [Describe(remote)],
        };

    private static Observation WcfTypePopulation(string type) =>
        RuleShapeGolden.Observation("infra.wcf-channel-type-population", "WCF Channel Analysis", new()) with { Subjects = [Type(type)] };

    private static MethodRef Method(string declaringType, string name) => new()
    {
        DeclaringType = Type(declaringType),
        Name = name,
        NormalizedSignature = "()",
        Fidelity = MatchFidelity.Exact,
    };

    private static Observation StateMachine(string declaringType, string method, long instances, long bytes, bool asyncVoid = false) =>
        RuleShapeGolden.Observation("type.async-state-machine", "Async State Machine Analysis", new()
        {
            ["instance.count"] = Count(instances),
            ["shallow.bytes"] = Bytes(bytes),
            ["is-async-void"] = Count(asyncVoid ? 1 : 0),
        }) with { Subjects = [Method(declaringType, method)] };

    private static Observation Closure(string type, long capturedBytes) =>
        RuleShapeGolden.Observation("type.async-captured-closure", "Async State Machine Analysis", new() { ["captured.bytes"] = Bytes(capturedBytes) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation WeakPopulation(long total, long dead, long deadKeys = 0, bool heldOnlyAvailable = false) =>
        RuleShapeGolden.Observation("weakreference.population", "Weak Reference Analysis", new()
        {
            ["total-handles.count"] = Count(total),
            ["dead-targets.count"] = Count(dead),
            ["object.count"] = Count(50),
            ["object.bytes"] = Bytes(2_000),
            ["dependent-dead-key.count"] = Count(deadKeys),
            ["held-only-detection-available"] = Count(heldOnlyAvailable ? 1 : 0),
        });

    private static Observation WeakTarget(string type, bool heldOnly) =>
        RuleShapeGolden.Observation("weakhandle.target", "Weak Reference Analysis", new() { ["held-only-via-weak-reference"] = Count(heldOnly ? 1 : 0) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation StaleWrapper() =>
        RuleShapeGolden.Observation("weakreference.stale-wrapper", "Weak Reference Analysis", new());

    private static Observation Retention(string type, long estimatedBytes, long instances) =>
        RuleShapeGolden.Observation("type.retention", "Dominator Analysis", new()
        {
            ["estimated-retained.bytes"] = Bytes(estimatedBytes),
            ["instance.count"] = Count(instances),
            ["gen2.count"] = Count(0),
            ["loh.bytes"] = Bytes(0),
            ["retained.bytes"] = Bytes(estimatedBytes),
            ["shallow.bytes"] = Bytes(estimatedBytes / 2),
        }) with { Subjects = [Type(type)] };

    private static Observation DomPopulation(long highlyReferenced, long approximated = 0, bool scanCapped = false) =>
        RuleShapeGolden.Observation("type.retention-population", "Dominator Analysis", new()
        {
            ["highly-referenced.count"] = Count(highlyReferenced),
            ["approximated-reference-addresses.count"] = Count(approximated),
            ["is-object-scan-capped"] = Count(scanCapped ? 1 : 0),
            ["is-reference-counting-skipped"] = Count(0),
            ["top-highly-referenced.has-root-path"] = Count(1),
            ["top-highly-referenced.is-root-path-search-truncated"] = Count(0),
            ["top-highly-referenced.evidence-signal.count"] = Count(1),
        });

    private static Observation Hotspot(string type, long incoming) =>
        RuleShapeGolden.Observation("type.retention-hotspot", "Dominator Analysis", new()
        {
            ["total-incoming-references.count"] = Count(incoming),
            ["object.count"] = Count(20),
            ["total.bytes"] = Bytes(30 * Mb),
        }) with { Subjects = [Type(type)] };
}
