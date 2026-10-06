using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 1: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class InfrastructureAndRuntimeRuleClaimTests
{
    // ── SqlCommand ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlCommand_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new SqlCommandSynthesisRule(),
        [
            new("warning-mostly-attached", [.. Repeat("infra.sql-command", "SQL Command Analysis", 100), CommandPopulation(total: 150, disposed: 50)]),
            new("warning-mostly-detached", [.. Repeat("infra.sql-command", "SQL Command Analysis", 100), CommandPopulation(total: 400, disposed: 300)]),
            new("critical", [.. Repeat("infra.sql-command", "SQL Command Analysis", 1_000), CommandPopulation(total: 1_100, disposed: 100)]),
        ]);
    }

    [Theory]
    [InlineData(100, 100, "warning threshold")]
    [InlineData(1_000, 1_000, "critical threshold")]
    public async Task SqlCommand_AssertsRetentionAgainstTheCrossedThreshold(int active, int budget, string basis)
    {
        Claim claim = await SingleAsync(new SqlCommandSynthesisRule(),
            [.. Repeat("infra.sql-command", "SQL Command Analysis", active), CommandPopulation(active + 50, 50)]);

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Retains);
        claim.Assertion.Measure!.Value.Value.Should().Be(active);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
        claim.Assertion.Reference.Basis.Should().Be(basis);
    }

    [Fact]
    public async Task SqlCommand_CounterIsEvidenceOnlyWhenMoreCommandsAreDetachedThanAttached()
    {
        Claim moreAttached = await SingleAsync(new SqlCommandSynthesisRule(),
            [.. Repeat("infra.sql-command", "SQL Command Analysis", 100), CommandPopulation(150, 50)]);
        Claim moreDetached = await SingleAsync(new SqlCommandSynthesisRule(),
            [.. Repeat("infra.sql-command", "SQL Command Analysis", 100), CommandPopulation(400, 300)]);

        moreAttached.Counter!.Statements.Should().BeEmpty();
        moreAttached.Counter.NoneFoundBecause.Should().NotBeNullOrWhiteSpace();
        moreDetached.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("300").And.Contain("100");
    }

    // ── SqlTransaction ───────────────────────────────────────────────────────

    [Fact]
    public async Task SqlTransaction_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new SqlTransactionSynthesisRule(),
        [
            new("warning-mostly-holding", [.. Repeat("infra.sql-transaction", "SQL Transaction Analysis", 5), TransactionPopulation(total: 8, disposed: 2, other: 1)]),
            new("warning-mostly-not-holding", [.. Repeat("infra.sql-transaction", "SQL Transaction Analysis", 5), TransactionPopulation(total: 40, disposed: 30, other: 5)]),
            new("critical", [.. Repeat("infra.sql-transaction", "SQL Transaction Analysis", 25), TransactionPopulation(total: 30, disposed: 3, other: 2)]),
        ]);
    }

    [Theory]
    [InlineData(5, 5, "warning threshold")]
    [InlineData(25, 25, "critical threshold")]
    public async Task SqlTransaction_AssertsRetentionAgainstTheCrossedThreshold(int active, int budget, string basis)
    {
        Claim claim = await SingleAsync(new SqlTransactionSynthesisRule(),
            [.. Repeat("infra.sql-transaction", "SQL Transaction Analysis", active), TransactionPopulation(active + 5, 3, 2)]);

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Retains);
        claim.Assertion.Measure!.Value.Value.Should().Be(active);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
        claim.Assertion.Reference.Basis.Should().Be(basis);
    }

    // ── SqlConnectionPool ────────────────────────────────────────────────────

    [Fact]
    public async Task SqlConnectionPool_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new SqlConnectionPoolSynthesisRule(),
        [
            new("warning", [Pool(current: 85, max: 100)]),
            new("critical-with-a-quiet-pool", [Pool(current: 10, max: 100), Pool(current: 96, max: 100)]),
        ]);
    }

    [Theory]
    [InlineData(85, 80.0, "near-capacity threshold")]
    [InlineData(96, 95.0, "critical utilisation threshold")]
    public async Task SqlConnectionPool_ExceedsTheThresholdItCrossed(int current, double budget, string basis)
    {
        Claim claim = await SingleAsync(new SqlConnectionPoolSynthesisRule(), [Pool(current, 100)]);

        claim.Assertion!.Predicate.Should().Be(AssertionPredicate.Exceeds);
        claim.Assertion.Measure!.Value.Value.Should().Be(current);
        claim.Assertion.Reference!.Value.Value.Should().Be(budget);
        claim.Assertion.Reference.Basis.Should().Be(basis);
    }

    [Fact]
    public async Task SqlConnectionPool_FalsificationNamesThePoolsActualMaxSize()
    {
        Claim claim = await SingleAsync(new SqlConnectionPoolSynthesisRule(), [Pool(100, 120)]);

        claim.FalsifiedBy.Should().Contain(test => test.Kind == FalsificationKind.CodeInspection && test.Test.Contains("120"));
    }

    // ── LockGraph ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LockGraph_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new LockGraphSynthesisRule(),
        [
            new("no-contention", [LockPressure(held: 2)]),
            new("contested-nobody-blocked", [LockPressure(held: 3), ContestedLock()]),
            new("contested-one-blocked", [LockPressure(held: 3), ContestedLock(), BlockedThread(managed: 11, os: 4_100)]),
            new("contested-two-blocked", [LockPressure(held: 3), ContestedLock(), BlockedThread(managed: 11, os: 4_100), BlockedThread(managed: 12, os: 4_104)]),
        ]);
    }

    [Fact]
    public async Task LockGraph_PredicateFollowsContention_AndCounterFollowsTheDirectionOfTheClaim()
    {
        Claim quiet = await SingleAsync(new LockGraphSynthesisRule(), [LockPressure(2)]);
        Claim blockedPair = await SingleAsync(
            new LockGraphSynthesisRule(), [LockPressure(3), ContestedLock(), BlockedThread(11, 4_100), BlockedThread(12, 4_104)]);

        quiet.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        quiet.FalsifiedBy.Should().ContainSingle().Which.Command.Should().Be("!syncblk");

        blockedPair.Assertion!.Predicate.Should().Be(AssertionPredicate.Blocks);
        blockedPair.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("heuristic");
        blockedPair.FalsifiedBy.Should().Contain(test => test.Command == "!dlk" && test.Test.Contains("11") && test.Test.Contains("12"));
    }

    // ── StaticRoot ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StaticRoot_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new StaticRootSynthesisRule(),
        [
            new("none", [StaticRootPopulation(rootCount: 0, retainedBytes: 0)]),
            new("warning", [StaticRootPopulation(3, 30 * Mb), StaticRoot(10 * Mb), StaticRoot(10 * Mb), StaticRoot(10 * Mb)]),
            new("critical-by-root-count", [StaticRootPopulation(12, 12 * Mb), .. Enumerable.Range(0, 12).Select(_ => StaticRoot(Mb))]),
            new("critical-by-largest-root", [StaticRootPopulation(2, 250 * Mb), StaticRoot(200 * Mb), StaticRoot(50 * Mb)]),
        ]);
    }

    [Fact]
    public async Task StaticRoot_ReferencesTheThresholdThatMadeItCritical_AndNoneWhenOnlyAWarning()
    {
        Claim warning = await SingleAsync(new StaticRootSynthesisRule(), [StaticRootPopulation(3, 30 * Mb), StaticRoot(10 * Mb)]);
        Claim byCount = await SingleAsync(new StaticRootSynthesisRule(), [StaticRootPopulation(12, 12 * Mb), StaticRoot(Mb)]);
        Claim byBytes = await SingleAsync(new StaticRootSynthesisRule(), [StaticRootPopulation(2, 250 * Mb), StaticRoot(200 * Mb)]);

        warning.Assertion!.Reference.Should().BeNull();
        byCount.Assertion!.Reference!.Basis.Should().Be("critical root-count threshold");
        byCount.Assertion.Measure!.Value.Value.Should().Be(12);
        byBytes.Assertion!.Reference!.Basis.Should().Be("critical per-root threshold");
        byBytes.Assertion.Measure!.Value.Value.Should().Be(200 * Mb);
    }

    [Fact]
    public async Task StaticRoot_AbsenceClaim_StatesItsOwnLimit()
    {
        Claim none = await SingleAsync(new StaticRootSynthesisRule(), [StaticRootPopulation(0, 0)]);

        none.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        none.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("significant");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const long Mb = 1_048_576;

    private static async Task<Claim> SingleAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations)
    {
        IReadOnlyList<Claim> claims = await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());
        return claims.Should().ContainSingle().Subject;
    }

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static IEnumerable<Observation> Repeat(string type, string analyzer, int count) =>
        Enumerable.Range(0, count).Select(_ => RuleShapeGolden.Observation(type, analyzer, new()));

    private static Observation CommandPopulation(long total, long disposed) =>
        RuleShapeGolden.Observation("infra.sql-command-population", "SQL Command Analysis", new()
        {
            ["total.count"] = Count(total),
            ["disposed.count"] = Count(disposed),
        });

    private static Observation TransactionPopulation(long total, long disposed, long other) =>
        RuleShapeGolden.Observation("infra.sql-transaction-population", "SQL Transaction Analysis", new()
        {
            ["total.count"] = Count(total),
            ["disposed.count"] = Count(disposed),
            ["other.count"] = Count(other),
        });

    private static Observation Pool(long current, long max) =>
        RuleShapeGolden.Observation("infra.sql-connection-pool", "SQL Connection Pool Analysis", new()
        {
            ["current.size"] = Count(current),
            ["max.size"] = Count(max),
        });

    private static Observation LockPressure(int held) =>
        RuleShapeGolden.Observation("runtime.lock-pressure", "Lock Graph Analysis", new() { ["held-lock.count"] = Count(held) });

    private static Observation ContestedLock() =>
        RuleShapeGolden.Observation("lock.contested", "Lock Graph Analysis", new() { ["recursion.count"] = Count(1) });

    private static Observation BlockedThread(int managed, uint os) =>
        RuleShapeGolden.Observation("thread.blocked", "Lock Graph Analysis", new()) with
        {
            Subjects = [new ThreadRef { OsThreadId = os, ManagedThreadId = managed, Fidelity = MatchFidelity.Exact }],
        };

    private static Observation StaticRootPopulation(long rootCount, long retainedBytes) =>
        RuleShapeGolden.Observation("static-root.population", "Static Root Leak Detector", new()
        {
            ["root.count"] = Count(rootCount),
            ["retained.bytes"] = Bytes(retainedBytes),
            ["top-root.has-root-path"] = Count(1),
            ["top-root.is-root-path-search-truncated"] = Count(0),
            ["top-root.evidence-signal.count"] = Count(1),
        });

    private static Observation StaticRoot(long retainedBytes) =>
        RuleShapeGolden.Observation("heap.static-root", "Static Root Leak Detector", new() { ["retained.bytes"] = Bytes(retainedBytes) });
}
