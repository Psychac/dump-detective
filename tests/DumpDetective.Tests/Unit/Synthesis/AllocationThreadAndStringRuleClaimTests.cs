using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 3: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class AllocationThreadAndStringRuleClaimTests
{
    private const long Mb = 1_048_576;

    // ── AllocationPattern ────────────────────────────────────────────────────

    [Fact]
    public async Task AllocationPattern_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new AllocationPatternSynthesisRule(),
        [
            new("critical-retained", [Allocation(gen0: 0.10, gen2: 0.80, loh: 0.20)]),
            new("high", [Allocation(gen0: 0.20, gen2: 0.40, loh: 0.10)]),
            new("moderate-transient", [Allocation(gen0: 0.75, gen2: 0.60, loh: 0.30)]),
            new("low-pressure-says-nothing", [Allocation(gen0: 0.90, gen2: 0.05, loh: 0.0)]),
        ]);
    }

    [Theory]
    [InlineData(0.10, 0.80, 0.20, 70.0, "critical pressure threshold")]
    [InlineData(0.20, 0.40, 0.10, 45.0, "high pressure threshold")]
    [InlineData(0.75, 0.60, 0.30, 20.0, "moderate pressure threshold")]
    public async Task AllocationPattern_ReferencesTheBandItLandedIn(double gen0, double gen2, double loh, double threshold, string basis)
    {
        Claim claim = (await RunAsync(new AllocationPatternSynthesisRule(), [Allocation(gen0, gen2, loh)])).Single();

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Exceeds);
        claim.Assertion.Reference!.Value.Value.Should().Be(threshold);
        claim.Assertion.Reference.Basis.Should().Be(basis);
        claim.Assertion.Measure!.Value.Value.Should().BeGreaterThan(threshold);
    }

    [Fact]
    public async Task AllocationPattern_CounterOnlyArguesChurnWhenTheProfileIsTransient()
    {
        Claim transient = (await RunAsync(new AllocationPatternSynthesisRule(), [Allocation(0.75, 0.60, 0.30)])).Single();
        Claim retained = (await RunAsync(new AllocationPatternSynthesisRule(), [Allocation(0.10, 0.80, 0.20)])).Single();

        transient.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("churn");
        retained.Counter!.Statements.Should().BeEmpty();
        retained.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
    }

    // ── Thread ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Thread_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ThreadSynthesisRule(),
        [
            new("quiet-summary-only", [Triage(alive: 20, blocked: 2, active: 12)]),
            new("summary-warning-by-blocked-count-with-active-threads", [Triage(40, blocked: 12, active: 20)]),
            new("summary-warning-by-exceptions", [Triage(20, 2, 12, exceptions: 3)]),
            new("blocked-ratio-no-active-work", [Triage(10, blocked: 9, active: 0)]),
            new("blocked-ratio-with-active-work", [Triage(10, blocked: 8, active: 2)]),
            new("finalizer-blocked", [Triage(20, 2, 12), FinalizerThread(blocked: true, locks: 2, managed: 3, os: 4_100)]),
            new("deep-async-chain", [Triage(20, 2, 12, asyncDepth: 14, asyncThreads: 2)]),
        ]);
    }

    [Fact]
    public async Task Thread_SummaryPredicateFollowsWhatMadeItAWarning()
    {
        IReadOnlyList<Claim> quiet = await RunAsync(new ThreadSynthesisRule(), [Triage(20, 2, 12)]);
        IReadOnlyList<Claim> byBlocked = await RunAsync(new ThreadSynthesisRule(), [Triage(40, 12, 20)]);
        IReadOnlyList<Claim> byException = await RunAsync(new ThreadSynthesisRule(), [Triage(20, 2, 12, exceptions: 3)]);

        quiet.Single().Assertion!.Predicate.Should().Be(AssertionPredicate.Composes);
        byBlocked.Single().Assertion!.Predicate.Should().Be(AssertionPredicate.Blocks);
        byBlocked.Single().Assertion.Reference!.Value.Value.Should().Be(10);
        byException.Single().Assertion!.Predicate.Should().Be(AssertionPredicate.Exhibits);
        byException.Single().Assertion.Measure!.Value.Value.Should().Be(3);
    }

    [Fact]
    public async Task Thread_FinalizerClaim_NamesTheFinalizerThreadAsItsSubject()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new ThreadSynthesisRule(), [Triage(20, 2, 12), FinalizerThread(true, 2, 3, 4_100)]);

        Claim finalizer = claims.Single(c => c.Fingerprint.EndsWith("finalizer-blocked"));
        finalizer.Assertion!.Subject.Should().BeOfType<ThreadRef>().Which.OsThreadId.Should().Be(4_100u);
        finalizer.Assertion.Predicate.Should().Be(AssertionPredicate.Blocks);
    }

    [Fact]
    public async Task Thread_BlockedRatioCounter_IsEvidenceOnlyWhenSomeThreadsAreActive()
    {
        IReadOnlyList<Claim> withActive = await RunAsync(new ThreadSynthesisRule(), [Triage(10, 8, 2)]);
        IReadOnlyList<Claim> noneActive = await RunAsync(new ThreadSynthesisRule(), [Triage(10, 9, 0)]);

        withActive.Single(c => c.Fingerprint.EndsWith("high-blocked-ratio")).Counter!.Statements.Should().ContainSingle();
        noneActive.Single(c => c.Fingerprint.EndsWith("high-blocked-ratio")).Counter!.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
    }

    // ── String ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task String_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new StringSynthesisRule(),
        [
            new("extreme-duplication-small-sample", [Strings(duplicationRatio: 0.7, patterns: 50, wastedBytes: 40 * Mb, coverage: 0.2)]),
            new("extreme-duplication-full-coverage", [Strings(0.7, 50, 40 * Mb, coverage: 1.0)]),
            new("high-duplicate-pressure", [Strings(0.3, 20, 5 * Mb, coverage: 1.0)]),
            new("loh-and-very-long", [Strings(0.0, 0, 0, loh: 12 * Mb, veryLong: 3, veryLongBytes: 9 * Mb)]),
            new("dominates-heap", [Strings(0.0, 0, 0, pctOfHeap: 35, totalString: 300 * Mb)]),
            new("low-sampling-coverage", [Strings(0.0, 0, 0, coverage: 0.03, sampled: 3_000)]),
        ]);
    }

    [Fact]
    public async Task String_DuplicationClaims_UseTheExtremeThresholdOnlyWhenTheyCrossedIt()
    {
        Claim extreme = (await RunAsync(new StringSynthesisRule(), [Strings(0.7, 50, 40 * Mb, coverage: 1.0)])).Single();
        Claim pressure = (await RunAsync(new StringSynthesisRule(), [Strings(0.3, 20, 5 * Mb, coverage: 1.0)])).Single();

        extreme.Assertion!.Predicate.Should().Be(AssertionPredicate.Duplicates);
        extreme.Assertion.Measure!.Value.Value.Should().BeApproximately(70, 0.001);
        extreme.Assertion.Reference!.Value.Value.Should().Be(50);
        pressure.Assertion!.Reference.Should().BeNull();
    }

    [Fact]
    public async Task String_DuplicationCounter_ArguesFromAPartialSample_AndNotFromAFullOne()
    {
        Claim sampled = (await RunAsync(new StringSynthesisRule(), [Strings(0.7, 50, 40 * Mb, coverage: 0.2)])).Single();
        Claim full = (await RunAsync(new StringSynthesisRule(), [Strings(0.7, 50, 40 * Mb, coverage: 1.0)])).Single();

        sampled.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("20.0%");
        full.Counter!.Statements.Should().BeEmpty();
        full.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task String_LowSamplingCoverage_IsNoLongerAClaim_ItIsCoverage()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new StringSynthesisRule(), [Strings(0.0, 0, 0, coverage: 0.03, sampled: 3_000)]);

        claims.Should().BeEmpty();
    }

    [Fact]
    public void StringCoverage_LowSampling_IsPartialWithTheStringsTheScanDidNotSee()
    {
        IReadOnlyList<CoverageRecord> records = new StringCoverageRule()
            .Evaluate(new ObservationMatchSet([Strings(0.0, 0, 0, coverage: 0.03, sampled: 3_000)]), new SynthesisContext());

        CoverageRecord record = records.Should().ContainSingle().Subject;
        record.Capability!.Value.Key.Should().Be(CapabilityVocabulary.HeapStrings);
        record.Status.Should().Be(CoverageStatus.Partial);
        record.Boundary!.Unit.Should().Be("strings");
        record.Boundary.Total.Should().NotBeNull();
        record.Boundary.Affected.Should().Be(record.Boundary.Total!.Value - 3_000);
    }

    [Fact]
    public void StringCoverage_SufficientSampling_ProducesNoRecord()
    {
        new StringCoverageRule()
            .Evaluate(new ObservationMatchSet([Strings(0.0, 0, 0, coverage: 0.5, sampled: 3_000)]), new SynthesisContext())
            .Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Observation Allocation(double gen0, double gen2, double loh) =>
        RuleShapeGolden.Observation("gc.allocation-composition", "Allocation Pattern Analysis", new()
        {
            ["gen0.count-fraction"] = Ratio(gen0),
            ["gen2.count-fraction"] = Ratio(gen2),
            ["gen0.size-fraction"] = Ratio(gen0),
            ["gen2.size-fraction"] = Ratio(gen2),
            ["loh.size-fraction"] = Ratio(loh),
            ["finalizable.type-count"] = Count(0),
            ["finalizable.bytes"] = Bytes(0),
            ["transient-classification.threshold"] = Ratio(70),
        });

    private static Observation Triage(
        int alive, int blocked, int active, int exceptions = 0, int asyncDepth = 0, int asyncThreads = 0) =>
        RuleShapeGolden.Observation("runtime.thread-triage", "Thread Analysis", new()
        {
            ["alive-thread.count"] = Count(alive),
            ["blocked-thread.count"] = Count(blocked),
            ["lock-holding-thread.count"] = Count(1),
            ["active-exception-thread.count"] = Count(exceptions),
            ["active-processing-thread.count"] = Count(active),
            ["async-chain-thread.count"] = Count(asyncThreads),
            ["max-async-chain.depth"] = Count(asyncDepth),
        });

    private static Observation FinalizerThread(bool blocked, int locks, int managed, uint os) =>
        RuleShapeGolden.Observation("runtime.finalizer-thread", "Thread Analysis", new()
        {
            ["is-blocked"] = Count(blocked ? 1 : 0),
            ["locks-held.count"] = Count(locks),
        }) with
        {
            Subjects = [new ThreadRef { OsThreadId = os, ManagedThreadId = managed, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation Strings(
        double duplicationRatio, int patterns, long wastedBytes, double coverage = 1.0, long loh = 0, int veryLong = 0,
        long veryLongBytes = 0, double pctOfHeap = 0, long totalString = 0, int sampled = 100_000) =>
        RuleShapeGolden.Observation("type.string-composition", "String Analysis", new()
        {
            ["duplication.ratio"] = Ratio(duplicationRatio),
            ["duplicate-pattern.count"] = Count(patterns),
            ["duplicate-wasted.bytes"] = Bytes(wastedBytes),
            ["loh-string.bytes"] = Bytes(loh),
            ["very-long-string.count"] = Count(veryLong),
            ["very-long-string.bytes"] = Bytes(veryLongBytes),
            ["pct-of-managed-heap.percent"] = Percent(pctOfHeap),
            ["total-string-memory.bytes"] = Bytes(totalString),
            ["sampling-coverage.ratio"] = Ratio(coverage),
            ["strings-sampled.count"] = Count(sampled),
            ["total-string.count"] = Count(sampled / Math.Max(coverage, 0.01)),
        });
}
