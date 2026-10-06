using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, cross-analyzer rules, first group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchOneClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public async Task HttpClientAccumulation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new HttpClientAccumulationInsightRule(),
        [
            new("mixed-usage", [Http(total: 100, clients: 5, responses: 40)]),
            new("too-few-clients-says-nothing", [Http(100, 2, 40)]),
            new("too-few-responses-says-nothing", [Http(100, 5, 5)]),
        ]);
    }

    [Fact]
    public async Task DynamicAssemblyAccumulation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new DynamicAssemblyAccumulationInsightRule(),
        [
            new("notice", [Modules(dynamicTotal: 50, anonymous: 3, domains: 2)]),
            new("warning", [Modules(150, 0, 1)]),
            new("below-threshold-says-nothing", [Modules(10, 0, 1)]),
        ]);
    }

    [Theory]
    [InlineData(150, "warning threshold", 100.0)]
    [InlineData(50, "notice threshold", 20.0)]
    public async Task DynamicAssemblyAccumulation_ReferencesTheThresholdThatSetItsSeverity(long dynamicTotal, string basis, double budget)
    {
        Claim claim = (await RunAsync(new DynamicAssemblyAccumulationInsightRule(), [Modules(dynamicTotal, 0, 1)])).Single();

        claim.Assertion!.Reference!.Basis.Should().Be(basis);
        claim.Assertion.Reference.Value.Value.Should().Be(budget);
    }

    [Fact]
    public async Task LohFragmentationInsight_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LohFragmentationInsightRule(),
        [
            new("warning", [LohSummary(percent: 40)]),
            new("critical", [LohSummary(70)]),
            new("below-threshold-says-nothing", [LohSummary(10)]),
        ]);
    }

    [Fact]
    public async Task StringDuplicationRatio_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new StringDuplicationRatioInsightRule(),
        [
            new("high-ratio-small-waste", [Strings(total: 1_000_000, ratio: 0.6, wasted: 2 * Mb, unique: 400_000, coverage: 0.9)]),
            new("high-waste-says-nothing-here", [Strings(1_000_000, 0.6, 20 * Mb, 400_000, 0.9)]),
            new("low-ratio-says-nothing", [Strings(1_000_000, 0.2, 2 * Mb, 800_000, 0.9)]),
        ]);
    }

    [Fact]
    public async Task StringDuplicationRatio_CounterStatesThatTheWasteIsSmall()
    {
        Claim claim = (await RunAsync(new StringDuplicationRatioInsightRule(), [Strings(1_000_000, 0.6, 2 * Mb, 400_000, 0.9)])).Single();

        claim.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("overstates the cost");
    }

    [Fact]
    public async Task JitHeapBloat_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new JitHeapBloatInsightRule(),
        [
            new("large-heap-many-threads", [Jit(heapBytes: 600 * Mb), Threads(alive: 150)]),
            new("large-heap-few-threads-says-nothing", [Jit(600 * Mb), Threads(50)]),
            new("small-heap-says-nothing", [Jit(100 * Mb), Threads(150)]),
        ]);
    }

    [Fact]
    public async Task JitHeapBloat_CitesBothSourcesSeparately_SoEachCanBeFollowed()
    {
        Observation jit = Jit(600 * Mb);
        Observation threads = Threads(150);

        Claim claim = (await RunAsync(new JitHeapBloatInsightRule(), [jit, threads])).Single();

        claim.Support.Should().HaveCount(2);
        claim.Support[0].Observations.Should().Equal(jit.Id);
        claim.Support[1].Observations.Should().Equal(threads.Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Observation Http(long total, long clients, long responses) =>
        RuleShapeGolden.Observation("infra.http-object-population", "HTTP Object Analysis", new()
        {
            ["total.count"] = Count(total),
            ["http-client.count"] = Count(clients),
            ["http-web-response.count"] = Count(responses),
        });

    private static Observation Modules(long dynamicTotal, long anonymous, long domains) =>
        RuleShapeGolden.Observation("runtime.module-population", "Module Analysis", new()
        {
            ["total-dynamic.count"] = Count(dynamicTotal),
            ["anonymous-module.count"] = Count(anonymous),
            ["total-domains.count"] = Count(domains),
        });

    private static Observation LohSummary(double percent) =>
        RuleShapeGolden.Observation("heap.loh-fragmentation-summary", "LOH & POH Fragmentation Analysis", new()
        {
            ["fragmentation.percent"] = Percent(percent),
            ["free.bytes"] = Bytes(40 * Mb),
            ["free-block.count"] = Count(300),
            ["largest-free-block.bytes"] = Bytes(4 * Mb),
        });

    private static Observation Strings(long total, double ratio, long wasted, long unique, double coverage) =>
        RuleShapeGolden.Observation("type.string-composition", "String Analysis", new()
        {
            ["total-string.count"] = Count(total),
            ["duplication.ratio"] = Ratio(ratio),
            ["duplicate-wasted.bytes"] = Bytes(wasted),
            ["unique-pattern.count"] = Count(unique),
            ["sampling-coverage.ratio"] = Ratio(coverage),
        });

    private static Observation Jit(long heapBytes) =>
        RuleShapeGolden.Observation("runtime.jit-activity", "JIT Analysis", new()
        {
            ["jit-heap.bytes"] = Bytes(heapBytes),
            ["jit-manager.count"] = Count(3),
        });

    private static Observation Threads(long alive) =>
        RuleShapeGolden.Observation("runtime.thread-triage", "Thread Analysis", new() { ["alive-thread.count"] = Count(alive) });
}
