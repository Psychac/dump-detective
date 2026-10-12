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

/// <summary>M1 step 4, cross-analyzer rules, second group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchTwoClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public void AnalyzerFailureCoverage_AnyFailure_IsAFailedRecordKeyedByAnalyzerAlone()
    {
        IReadOnlyList<CoverageRecord> records = new AnalyzerFailureCoverageRule()
            .Evaluate(new ObservationMatchSet([]), new SynthesisContext { FailedAnalyzers = ["Dominator Analysis"] });

        CoverageRecord record = records.Should().ContainSingle().Subject;
        record.Capability.Should().BeNull();
        record.Artifact.Should().BeNull();
        record.Status.Should().Be(CoverageStatus.Failed);
        record.Impacts.Should().ContainSingle().Which.Should().Be(new CoverageImpact("Dominator Analysis", CoverageImpactKind.Failed));
    }

    [Fact]
    public void AnalyzerFailureCoverage_OneRecordPerFailedAnalyzer_AndNoneWhenNothingFailed()
    {
        var rule = new AnalyzerFailureCoverageRule();

        rule.Evaluate(new ObservationMatchSet([]), new SynthesisContext { FailedAnalyzers = ["A", "B"] }).Should().HaveCount(2);
        rule.Evaluate(new ObservationMatchSet([]), new SynthesisContext()).Should().BeEmpty();
    }

    [Fact]
    public async Task LohPressureInsight_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LohPressureInsightRule(),
        [
            new("warning-from-memory", [Memory(30)]),
            new("critical-from-generations", [Generations(45)]),
            new("below-threshold-says-nothing", [Memory(10)]),
        ]);
    }

    [Fact]
    public async Task PinnedHandlePressure_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new PinnedHandlePressureInsightRule(),
        [
            new("pinning-alone", [Handles(pinned: 150)]),
            new("pinning-with-fragmentation", [Handles(150), LohSummary(40)]),
            new("pinning-with-low-fragmentation", [Handles(150), LohSummary(5)]),
            new("below-threshold-says-nothing", [Handles(50)]),
        ]);
    }

    [Fact]
    public async Task PinnedHandlePressure_CounterWarnsThatFragmentationHasOtherCauses_OnlyWhenCorrelated()
    {
        Claim correlated = (await RunAsync(new PinnedHandlePressureInsightRule(), [Handles(150), LohSummary(40)])).Single();
        Claim alone = (await RunAsync(new PinnedHandlePressureInsightRule(), [Handles(150)])).Single();

        correlated.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("short-lived large allocations");
        correlated.Assertion!.Qualifiers.Mechanism.Should().Contain("40.0%");
        alone.Counter!.Statements.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleWeakReference_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new StaleWeakReferenceAccumulationInsightRule(),
        [
            new("info", [WeakPopulation(total: 100, dead: 60, stale: 4)]),
            new("warning", [WeakPopulation(100, 90, 0)]),
            new("below-threshold-says-nothing", [WeakPopulation(100, 20, 0)]),
        ]);
    }

    [Fact]
    public async Task WcfChannelFault_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new WcfChannelFaultInsightRule(),
        [
            new("faulted-with-communication-exceptions", [Wcf(total: 20, faulted: 3), ExceptionType("System.ServiceModel.CommunicationException", 7), ExceptionType("System.ObjectDisposedException", 2), ExceptionType("System.ArgumentException", 50)]),
            new("faulted-without-matching-exceptions-says-nothing", [Wcf(20, 3), ExceptionType("System.ArgumentException", 50)]),
            new("no-faulted-says-nothing", [Wcf(20, 0), ExceptionType("System.ServiceModel.CommunicationException", 7)]),
        ]);
    }

    [Fact]
    public async Task WcfChannelFault_CitesTheChannelPopulationAndTheExceptionsSeparately()
    {
        Observation wcf = Wcf(20, 3);
        Observation exception = ExceptionType("System.ServiceModel.CommunicationException", 7);

        Claim claim = (await RunAsync(new WcfChannelFaultInsightRule(), [wcf, exception])).Single();

        claim.Support.Should().HaveCount(2);
        claim.Support[0].Observations.Should().Equal(wcf.Id);
        claim.Support[1].Observations.Should().Equal(exception.Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Observation Memory(double lohPercent) =>
        RuleShapeGolden.Observation("heap.memory-composition", "Memory Analysis", new()
        {
            ["loh.percent"] = Percent(lohPercent),
            ["loh.bytes"] = Bytes(200 * Mb),
        });

    private static Observation Generations(double lohPercent) =>
        RuleShapeGolden.Observation("gc.generation-composition", "GC Generation Analysis", new()
        {
            ["loh.percent"] = Percent(lohPercent),
            ["loh.bytes"] = Bytes(300 * Mb),
        });

    private static Observation Handles(long pinned) =>
        RuleShapeGolden.Observation("handle.population", "GC Handle Analysis", new() { ["pinned-target.count"] = Count(pinned) });

    private static Observation LohSummary(double percent) =>
        RuleShapeGolden.Observation("heap.loh-fragmentation-summary", "LOH & POH Fragmentation Analysis", new() { ["fragmentation.percent"] = Percent(percent) });

    private static Observation WeakPopulation(long total, long dead, long stale) =>
        RuleShapeGolden.Observation("weakreference.population", "Weak Reference Analysis", new()
        {
            ["total-handles.count"] = Count(total),
            ["dead-targets.count"] = Count(dead),
            ["stale-wrapper.count"] = Count(stale),
            ["stale-wrapper.is-exact"] = Count(1),
        });

    private static Observation Wcf(long total, long faulted) =>
        RuleShapeGolden.Observation("infra.wcf-channel-population", "WCF Channel Analysis", new()
        {
            ["total.count"] = Count(total),
            ["faulted.count"] = Count(faulted),
        });

    private static Observation ExceptionType(string type, long instances) =>
        RuleShapeGolden.Observation("runtime.exception-type", "Crash Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [new TypeRef { CanonicalName = type, Fidelity = MatchFidelity.Exact }],
        };
}
