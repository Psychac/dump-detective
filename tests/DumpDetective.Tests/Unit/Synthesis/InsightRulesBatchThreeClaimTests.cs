using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, cross-analyzer rules, third group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchThreeClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public async Task FinalizerQueueBacklog_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new FinalizerQueueBacklogInsightRule(),
        [
            new("warning-thread-running", [Queue(2_000, 50 * Mb), FinalizerThread(blocked: false)]),
            new("critical-thread-blocked", [Queue(2_000, 50 * Mb), FinalizerThread(blocked: true)]),
            new("critical-by-count-no-thread-observation", [Queue(20_000, 0)]),
            new("below-threshold-says-nothing", [Queue(500, 0)]),
        ]);
    }

    [Fact]
    public async Task FinalizerQueueBacklog_CounterIsEvidenceOnlyWhenTheFinalizerThreadIsRunning()
    {
        Claim running = (await RunAsync(new FinalizerQueueBacklogInsightRule(), [Queue(2_000, 0), FinalizerThread(false)])).Single();
        Claim blocked = (await RunAsync(new FinalizerQueueBacklogInsightRule(), [Queue(2_000, 0), FinalizerThread(true)])).Single();

        running.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("not blocked");
        blocked.Counter!.Statements.Should().BeEmpty();
        blocked.Assertion!.Qualifiers.Mechanism.Should().Contain("blocked");
    }

    [Fact]
    public async Task LohArrayPressure_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LohArrayPressureInsightRule(),
        [
            new("arrays-alone", [Arrays(300 * Mb, 12)]),
            new("arrays-with-fragmentation", [Arrays(300 * Mb, 12), LohSummary(40)]),
            new("below-threshold-says-nothing", [Arrays(100 * Mb, 4)]),
        ]);
    }

    [Fact]
    public async Task PinnedStringLeak_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new PinnedStringLeakInsightRule(),
        [
            new("warning", [PinnedBytes("System.String", 5 * Mb), PinnedType("System.String", 40)]),
            new("critical", [PinnedBytes("System.String", 25 * Mb), PinnedType("System.String", 400)]),
            new("other-type-says-nothing", [PinnedBytes("System.Byte[]", 25 * Mb), PinnedType("System.Byte[]", 400)]),
            new("below-threshold-says-nothing", [PinnedBytes("System.String", 100), PinnedType("System.String", 2)]),
        ]);
    }

    [Fact]
    public async Task ActiveCrashInsight_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ActiveCrashInsightRule(),
        [
            new("active-with-dominant-type", [CrashPopulation(active: 3), ActiveException("System.InvalidOperationException"), ActiveException("System.InvalidOperationException"), ActiveException("System.TimeoutException")]),
            new("active-count-only", [CrashPopulation(active: 2)]),
            new("none-says-nothing", [CrashPopulation(active: 0)]),
        ]);
    }

    [Fact]
    public async Task ActiveCrashInsight_NamesTheDominantExceptionType()
    {
        Claim claim = (await RunAsync(new ActiveCrashInsightRule(),
            [CrashPopulation(3), ActiveException("System.InvalidOperationException"), ActiveException("System.InvalidOperationException"), ActiveException("System.TimeoutException")])).Single();

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Faults);
        claim.Assertion.Qualifiers.Mechanism.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task DbConnectionLeak_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new DbConnectionLeakInsightRule(),
        [
            new("exhaustion-suspected", [DbPopulation(total: 60, open: 40), ExceptionType("System.TimeoutException", 4), ExceptionType("System.InvalidOperationException", 3), ExceptionType("System.ArgumentException", 90)]),
            new("too-few-timeouts-says-nothing", [DbPopulation(60, 40), ExceptionType("System.TimeoutException", 2)]),
            new("too-few-open-says-nothing", [DbPopulation(60, 5), ExceptionType("System.TimeoutException", 10)]),
        ]);
    }

    [Fact]
    public async Task DbConnectionLeak_AdmitsThatTimeoutsHaveOtherCauses_AndCitesBothSources()
    {
        Observation db = DbPopulation(60, 40);
        Observation timeout = ExceptionType("System.TimeoutException", 8);

        Claim claim = (await RunAsync(new DbConnectionLeakInsightRule(), [db, timeout])).Single();

        claim.Counter!.NoneFoundBecause.Should().Contain("many causes");
        claim.Support.Should().HaveCount(2);
        claim.Support[0].Observations.Should().Equal(db.Id);
        claim.Support[1].Observations.Should().Equal(timeout.Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static Observation Queue(long count, long retained) =>
        RuleShapeGolden.Observation("finalizer.queue-population", "Finalizable Object Analysis", new()
        {
            ["queue.count"] = Count(count),
            ["retained.bytes"] = Bytes(retained),
        });

    private static Observation FinalizerThread(bool blocked) =>
        RuleShapeGolden.Observation("runtime.finalizer-thread", "Thread Analysis", new() { ["is-blocked"] = Count(blocked ? 1 : 0) });

    private static Observation Arrays(long lohBytes, long lohCount) =>
        RuleShapeGolden.Observation("type.array-population", "Array Analysis", new()
        {
            ["loh.bytes"] = Bytes(lohBytes),
            ["loh.count"] = Count(lohCount),
        });

    private static Observation LohSummary(double percent) =>
        RuleShapeGolden.Observation("heap.loh-fragmentation-summary", "LOH & POH Fragmentation Analysis", new() { ["fragmentation.percent"] = Percent(percent) });

    private static Observation PinnedBytes(string type, long retained) =>
        RuleShapeGolden.Observation("handle.pinned-target-bytes", "GC Handle Analysis", new() { ["retained.bytes"] = Bytes(retained) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation PinnedType(string type, long instances) =>
        RuleShapeGolden.Observation("handle.pinned-target-type", "GC Handle Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation CrashPopulation(long active) =>
        RuleShapeGolden.Observation("runtime.crash-population", "Crash Analysis", new() { ["active.count"] = Count(active) });

    private static Observation ActiveException(string type) =>
        RuleShapeGolden.Observation("runtime.active-exception", "Crash Analysis", new()) with { Subjects = [Type(type)] };

    private static Observation DbPopulation(long total, long open) =>
        RuleShapeGolden.Observation("infra.db-connection-population", "DB Connection Analysis", new()
        {
            ["total.count"] = Count(total),
            ["open.count"] = Count(open),
        });

    private static Observation ExceptionType(string type, long instances) =>
        RuleShapeGolden.Observation("runtime.exception-type", "Crash Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
        };
}
