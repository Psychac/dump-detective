using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, cross-analyzer rules, fourth group: the structured claim each rule authors, pinned by golden and by direction checks.</summary>
public sealed class InsightRulesBatchFourClaimTests
{
    private const long Mb = 1_048_576;

    [Fact]
    public async Task RecurringTimeoutPattern_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new RecurringTimeoutPatternInsightRule(),
        [
            new("notice", [CrashPopulation(total: 100), ExceptionType("System.TimeoutException", 12), ExceptionType("System.ObjectDisposedException", 6)]),
            new("warning", [CrashPopulation(100), ExceptionType("System.OperationCanceledException", 25)]),
            new("below-threshold-says-nothing", [CrashPopulation(100), ExceptionType("System.TimeoutException", 3)]),
        ]);
    }

    [Theory]
    [InlineData(25, "warning threshold", 20.0)]
    [InlineData(12, "notice threshold", 10.0)]
    public async Task RecurringTimeoutPattern_ReferencesTheThresholdThatSetItsSeverity(long timeouts, string basis, double budget)
    {
        Claim claim = (await RunAsync(new RecurringTimeoutPatternInsightRule(), [CrashPopulation(100), ExceptionType("System.TimeoutException", timeouts)])).Single();

        claim.Assertion!.Reference!.Basis.Should().Be(basis);
        claim.Assertion.Reference.Value.Value.Should().Be(budget);
    }

    [Fact]
    public async Task WcfOpeningTimeoutCorrelation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new WcfOpeningTimeoutCorrelationInsightRule(),
        [
            new("opening-with-timeouts", [Wcf(total: 20, opening: 6), ExceptionType("System.TimeoutException", 4)]),
            new("no-opening-says-nothing", [Wcf(20, 0), ExceptionType("System.TimeoutException", 4)]),
            new("no-timeouts-says-nothing", [Wcf(20, 6), ExceptionType("System.ArgumentException", 4)]),
        ]);
    }

    [Fact]
    public async Task EventLeakPattern_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new EventLeakPatternInsightRule(),
        [
            new("with-high-gen2", [EventLeaks(groups: 3, subscribers: 500), Generations(gen2: 55)]),
            new("with-large-finalizer-queue", [EventLeaks(3, 500), FinalizerQueue(2_500)]),
            new("with-both", [EventLeaks(3, 500), Generations(55), FinalizerQueue(2_500)]),
            new("no-correlating-signal-says-nothing", [EventLeaks(3, 500), Generations(10), FinalizerQueue(100)]),
        ]);
    }

    [Fact]
    public async Task EventLeakPattern_NamesWhichSignalsCorrelated()
    {
        Claim gen2 = (await RunAsync(new EventLeakPatternInsightRule(), [EventLeaks(3, 500), Generations(55)])).Single();
        Claim both = (await RunAsync(new EventLeakPatternInsightRule(), [EventLeaks(3, 500), Generations(55), FinalizerQueue(2_500)])).Single();

        gen2.Assertion!.Qualifiers.Mechanism.Should().Be("Gen2 pressure");
        both.Assertion!.Qualifiers.Mechanism.Should().Be("Gen2 and finalizer queue pressure");
        both.Support.Should().HaveCount(3);
    }

    [Fact]
    public async Task FatalExceptionOnHeap_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new FatalExceptionOnHeapInsightRule(),
        [
            new("out-of-memory-and-stack-overflow", [CrashPopulation(10), ExceptionType("System.OutOfMemoryException", 2), ExceptionType("System.StackOverflowException", 1), ExceptionType("System.ArgumentException", 7)]),
            new("none-fatal-says-nothing", [CrashPopulation(10), ExceptionType("System.ArgumentException", 7)]),
        ]);
    }

    [Fact]
    public async Task LeakSuspicion_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LeakSuspicionInsightRule(),
        [
            new("highly-referenced-and-duplicate-strings", [Retention(highlyReferenced: 9), StringsComposition(wasted: 20 * Mb)]),
            new("highly-referenced-only", [Retention(9), StringsComposition(wasted: 2 * Mb)]),
            new("duplicate-strings-only", [Retention(0), StringsComposition(20 * Mb)]),
            new("neither-says-nothing", [Retention(0), StringsComposition(2 * Mb)]),
        ]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Observation CrashPopulation(long total) =>
        RuleShapeGolden.Observation("runtime.crash-population", "Crash Analysis", new() { ["total.count"] = Count(total) });

    private static Observation ExceptionType(string type, long instances) =>
        RuleShapeGolden.Observation("runtime.exception-type", "Crash Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [new TypeRef { CanonicalName = type, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation Wcf(long total, long opening) =>
        RuleShapeGolden.Observation("infra.wcf-channel-population", "WCF Channel Analysis", new()
        {
            ["total.count"] = Count(total),
            ["opening.count"] = Count(opening),
        });

    private static Observation EventLeaks(long groups, long subscribers) =>
        RuleShapeGolden.Observation("event.publisher-leak-population", "Event Leak Analysis", new()
        {
            ["total.count"] = Count(groups),
            ["subscriber.count"] = Count(subscribers),
        });

    private static Observation Generations(double gen2) =>
        RuleShapeGolden.Observation("gc.generation-composition", "GC Generation Analysis", new() { ["gen2.percent"] = Percent(gen2) });

    private static Observation FinalizerQueue(long count) =>
        RuleShapeGolden.Observation("finalizer.queue-population", "Finalizable Object Analysis", new() { ["queue.count"] = Count(count) });

    private static Observation Retention(long highlyReferenced) =>
        RuleShapeGolden.Observation("type.retention-population", "Dominator Analysis", new() { ["highly-referenced.count"] = Count(highlyReferenced) });

    private static Observation StringsComposition(long wasted) =>
        RuleShapeGolden.Observation("type.string-composition", "String Analysis", new()
        {
            ["duplicate-wasted.bytes"] = Bytes(wasted),
            ["duplicate-pattern.count"] = Count(300),
            ["total-string.count"] = Count(1_000_000),
            ["total-string-memory.bytes"] = Bytes(200 * Mb),
        });
}
