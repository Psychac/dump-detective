using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, cross-analyzer rules, sixth group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchSixClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public async Task StaticRootFinalizableCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new StaticRootFinalizableCorrelationInsightRule(),
        [
            new("overlap", [RetainedType("App.Handle", "static App.Cache.Items", 40), .. QueueEntries("App.Handle", 5)]),
            new("picks-the-type-with-most-queued", [RetainedType("App.A", "static A", 10), RetainedType("App.B", "static B", 10), .. QueueEntries("App.A", 2), .. QueueEntries("App.B", 7)]),
            new("no-overlap-says-nothing", [RetainedType("App.Handle", "static App.Cache.Items", 40), .. QueueEntries("App.Other", 5)]),
        ]);
    }

    [Fact]
    public async Task AsyncStateMachineFireAndForget_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AsyncStateMachineFireAndForgetInsightRule(),
        [
            new("worst-method-with-population-and-heap-share", [StateMachine("App.Worker", "RunAsync", 150), StateMachine("App.Worker", "PumpAsync", 20), SmPopulation(total: 200, bytes: 30 * Mb), HeapMemory(1_000 * Mb)]),
            new("worst-method-alone", [StateMachine("App.Worker", "RunAsync", 150)]),
            new("below-threshold-says-nothing", [StateMachine("App.Worker", "RunAsync", 50)]),
        ]);
    }

    [Fact]
    public async Task AsyncStateMachineFireAndForget_SubjectIsTheWorstMethod_AndItsStateMachinesAreCitedTogether()
    {
        Observation first = StateMachine("App.Worker", "RunAsync", 90);
        Observation second = StateMachine("App.Worker", "RunAsync", 60);

        Claim claim = (await RunAsync(new AsyncStateMachineFireAndForgetInsightRule(), [first, second])).Single();

        claim.Assertion!.Subject.Should().BeOfType<MethodRef>().Which.Name.Should().Be("RunAsync");
        claim.Assertion.Measure!.Value.Value.Should().Be(150);
        claim.Support[0].Observations.Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task DataTableLifecycle_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new DataTableLifecycleInsightRule(),
        [
            new("columns-in-finalizer-queue", [.. QueueEntries("System.Data.DataColumn", 120), .. QueueEntries("System.Data.DataTable", 5)]),
            new("rows-on-heap", [KnownPattern("System.Data.DataRow", 20_000)]),
            new("below-threshold-says-nothing", [.. QueueEntries("System.Data.DataColumn", 10), KnownPattern("System.Data.DataRow", 100)]),
        ]);
    }

    [Fact]
    public async Task GCRootLargeRetention_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new GCRootLargeRetentionInsightRule(),
        [
            new("one-large-root-with-kind-summaries", [Root("App.Cache", HeapRootKind.StrongHandle, 120 * Mb), Root("App.Small", HeapRootKind.StaticVar, Mb), KindSummary(HeapRootKind.StrongHandle, 130 * Mb), KindSummary(HeapRootKind.StaticVar, 2 * Mb)]),
            new("one-large-root-alone", [Root("App.Cache", HeapRootKind.Stack, 80 * Mb)]),
            new("below-threshold-says-nothing", [Root("App.Cache", HeapRootKind.Stack, 10 * Mb)]),
        ]);
    }

    [Fact]
    public async Task ReferenceChainDominatorCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ReferenceChainDominatorCorrelationInsightRule(),
        [
            new("warning", [HeapRetention(1_000 * Mb), ChainSample("App.Cache", 150 * Mb)]),
            new("critical-with-two-offenders", [HeapRetention(1_000 * Mb), ChainSample("App.Cache", 300 * Mb), ChainSample("App.Index", 120 * Mb)]),
            new("below-threshold-says-nothing", [HeapRetention(1_000 * Mb), ChainSample("App.Cache", 20 * Mb)]),
        ]);
    }

    [Theory]
    [InlineData(300, "critical share threshold", 25.0)]
    [InlineData(150, "warning share threshold", 10.0)]
    public async Task ReferenceChainDominatorCorrelation_ReferencesTheThresholdThatSetItsSeverity(long retainedMb, string basis, double budget)
    {
        Claim claim = (await RunAsync(new ReferenceChainDominatorCorrelationInsightRule(), [HeapRetention(1_000 * Mb), ChainSample("App.Cache", retainedMb * Mb)])).Single();

        claim.Assertion!.Reference!.Basis.Should().Be(basis);
        claim.Assertion.Reference.Value.Value.Should().Be(budget);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation RetainedType(string type, string root, long instances) =>
        RuleShapeGolden.Observation("heap.static-root-retained-type", "Static Root Leak Detector", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
            Evidence = [Describe(root)],
        };

    private static IEnumerable<Observation> QueueEntries(string type, int count) =>
        Enumerable.Range(0, count).Select(_ =>
            RuleShapeGolden.Observation("finalizer.queue-entry", "Finalizable Object Analysis", new()) with { Subjects = [Type(type)] });

    private static Observation StateMachine(string declaringType, string method, long instances) =>
        RuleShapeGolden.Observation("type.async-state-machine", "Async State Machine Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects =
            [
                Type(declaringType + "+<" + method + ">d__0"),
                new MethodRef { DeclaringType = Type(declaringType), Name = method, NormalizedSignature = "()", Fidelity = MatchFidelity.Exact },
            ],
        };

    private static Observation SmPopulation(long total, long bytes) =>
        RuleShapeGolden.Observation("type.async-state-machine-population", "Async State Machine Analysis", new()
        {
            ["total.count"] = Count(total),
            ["total.bytes"] = Bytes(bytes),
        });

    private static Observation HeapMemory(long totalBytes) =>
        RuleShapeGolden.Observation("heap.memory-composition", "Memory Analysis", new() { ["total.bytes"] = Bytes(totalBytes) });

    private static Observation KnownPattern(string type, long instances) =>
        RuleShapeGolden.Observation("type.known-pattern-population", "Memory Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation Root(string targetType, HeapRootKind kind, long retained) =>
        RuleShapeGolden.Observation("heap.root", "GC Root Analysis", new()
        {
            ["retained.bytes"] = Bytes(retained),
            ["root-kind.ordinal"] = Count((int)kind),
        }) with { Subjects = [Type(targetType)] };

    private static Observation KindSummary(HeapRootKind kind, long retained) =>
        RuleShapeGolden.Observation("root.kind-summary", "GC Root Analysis", new()
        {
            ["retained.bytes"] = Bytes(retained),
            ["kind.ordinal"] = Count((int)kind),
        });

    private static Observation HeapRetention(long totalBytes) =>
        RuleShapeGolden.Observation("type.retention-population", "Dominator Analysis", new() { ["total.bytes"] = Bytes(totalBytes) });

    private static Observation ChainSample(string type, long retained) =>
        RuleShapeGolden.Observation("reference-chain.type-sample", "Reference Chain Analysis", new() { ["retained.bytes"] = Bytes(retained) }) with
        {
            Subjects = [Type(type)],
        };
}
