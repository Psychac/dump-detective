using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>M1 step 4, batch 5: the structured claim each of these rules authors, pinned by golden and by direction checks.</summary>
public sealed class StabilityAndHeapLayoutRuleClaimTests
{
    private const long Mb = 1_048_576;
    private const long Gb = 1_073_741_824;

    // ── Crash ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Crash_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new CrashSynthesisRule(),
        [
            new("none", [CrashPopulation(total: 0, active: 0, unique: 0)]),
            new("pressure-without-active", [CrashPopulation(5, 0, 2)]),
            new("active-with-candidate", [CrashPopulation(5, 2, 2), CrashCandidate("System.InvalidOperationException", active: 2, tier: "Exact")]),
            new("active-without-candidate", [CrashPopulation(5, 2, 2), ActiveException("System.TimeoutException"), ActiveException("System.TimeoutException")]),
        ]);
    }

    [Fact]
    public async Task Crash_PredicateFollowsWhetherAnyExceptionIsActive()
    {
        Claim none = (await RunAsync(new CrashSynthesisRule(), [CrashPopulation(0, 0, 0)])).Single();
        Claim pressure = (await RunAsync(new CrashSynthesisRule(), [CrashPopulation(5, 0, 2)])).Single();
        Claim active = (await RunAsync(new CrashSynthesisRule(), [CrashPopulation(5, 2, 2), CrashCandidate("System.InvalidOperationException", 2, "Exact")])).Single();

        none.Assertion!.Predicate.Should().Be(AssertionPredicate.Normal);
        none.Counter!.Statements.Should().ContainSingle();
        pressure.Assertion!.Predicate.Should().Be(AssertionPredicate.Exhibits);
        active.Assertion!.Predicate.Should().Be(AssertionPredicate.Faults);
        active.Assertion.Qualifiers.Mechanism.Should().Contain("InvalidOperationException");
    }

    // ── SegmentReservation ───────────────────────────────────────────────────

    [Fact]
    public async Task SegmentReservation_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new SegmentReservationSynthesisRule(),
        [
            new("everything-fires-64-bit", [Reservation(ratio: 5, ephemeralFill: 95), Region(nearEmpty: 3, committed: 60 * Mb)]),
            new("32-bit-by-reservation", [Reservation(ratio: 1, pointerSize: 4, reserved: 3 * Gb)]),
            new("medium-ratio-only", [Reservation(ratio: 3)]),
            new("regions-below-the-warning-size", [Reservation(ratio: 1), Region(nearEmpty: 2, committed: 10 * Mb)]),
            new("quiet", [Reservation(ratio: 1.1)]),
        ]);
    }

    [Fact]
    public async Task SegmentReservation_AddressSpaceClaimNamesWhichLimitItCrossed()
    {
        Claim by32Bit = (await RunAsync(new SegmentReservationSynthesisRule(), [Reservation(1, pointerSize: 4, reserved: 3 * Gb)]))
            .Single(c => c.Fingerprint.EndsWith("address-space-pressure"));
        Claim byRatio = (await RunAsync(new SegmentReservationSynthesisRule(), [Reservation(5)]))
            .Single(c => c.Fingerprint.EndsWith("address-space-pressure"));

        by32Bit.Assertion!.Reference!.Basis.Should().Be("32-bit address-space threshold");
        byRatio.Assertion!.Reference!.Basis.Should().Be("high-pressure threshold");
        byRatio.Assertion.Measure!.Value.Value.Should().Be(5);
    }

    [Fact]
    public async Task SegmentReservation_NearEmptyRegions_ReferenceTheWarningSizeOnlyWhenTheyCrossedIt()
    {
        Claim large = (await RunAsync(new SegmentReservationSynthesisRule(), [Reservation(1), Region(3, 60 * Mb)])).Single(c => c.Fingerprint.EndsWith("near-empty-regions"));
        Claim small = (await RunAsync(new SegmentReservationSynthesisRule(), [Reservation(1), Region(2, 10 * Mb)])).Single(c => c.Fingerprint.EndsWith("near-empty-regions"));

        large.Assertion!.Reference.Should().NotBeNull();
        small.Assertion!.Reference.Should().BeNull();
    }

    // ── DbConnection ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DbConnection_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new DbConnectionSynthesisRule(),
        [
            new("count-warning-mostly-closed", [DbPopulation(total: 60, closed: 40, broken: 0), .. Connections(generation: 0, 10), DbType("System.Data.SqlClient.SqlConnection", 60)]),
            new("count-warning-mostly-open", [DbPopulation(60, 5, 0), .. Connections(0, 10)]),
            new("everything-fires", [DbPopulation(250, 100, 6), .. Connections(0, 15), .. Connections(2, 5), DbType("System.Data.SqlClient.SqlConnection", 250)]),
        ]);
    }

    [Fact]
    public async Task DbConnection_CountClaimReferencesTheThresholdThatSetItsSeverity()
    {
        Claim warning = (await RunAsync(new DbConnectionSynthesisRule(), [DbPopulation(60, 5, 0), .. Connections(0, 10)])).Single();
        Claim critical = (await RunAsync(new DbConnectionSynthesisRule(), [DbPopulation(250, 100, 0), .. Connections(0, 10)])).Single();

        warning.Assertion!.Reference!.Value.Value.Should().Be(50);
        critical.Assertion!.Reference!.Value.Value.Should().Be(200);
        critical.Assertion.Reference.Basis.Should().Be("critical threshold");
    }

    [Fact]
    public async Task DbConnection_CountCounterIsEvidenceOnlyWhenMostConnectionsAreClosed()
    {
        Claim mostlyClosed = (await RunAsync(new DbConnectionSynthesisRule(), [DbPopulation(60, 40, 0), .. Connections(0, 10)])).Single();
        Claim mostlyOpen = (await RunAsync(new DbConnectionSynthesisRule(), [DbPopulation(60, 5, 0), .. Connections(0, 10)])).Single();

        mostlyClosed.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("40");
        mostlyOpen.Counter!.Statements.Should().BeEmpty();
    }

    // ── Array ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Array_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new ArraySynthesisRule(),
        [
            new("loh-warning-with-a-largest-array", [ArrayComposition(lohBytes: 600 * Mb, lohCount: 12), LargeArray("System.Byte")]),
            new("loh-critical", [ArrayComposition(lohBytes: 2_500 * Mb, lohCount: 40)]),
            new("multidim-by-count", [ArrayComposition(rank: 2, instances: 1_500, shallowBytes: 10 * Mb)]),
            new("multidim-by-bytes", [ArrayComposition(rank: 2, instances: 10, shallowBytes: 150 * Mb)]),
            new("two-sparse-arrays", [SparseArray("System.Object", length: 100_000, ratio: 0.9, wasted: 20 * Mb, address: 4096), SparseArray("System.String", 50_000, 0.8, 12 * Mb, 8192)]),
            new("pinned-by-count", [ArrayComposition(pinnedCount: 150, pinnedBytes: 10 * Mb), PinnedArray("System.Byte")]),
            new("pinned-by-size", [ArrayComposition(pinnedCount: 5, pinnedBytes: 60 * Mb)]),
            new("pinned-critical", [ArrayComposition(pinnedCount: 5, pinnedBytes: 300 * Mb)]),
        ]);
    }

    [Theory]
    [InlineData(600, AssertionPredicate.Exceeds, 500)]
    [InlineData(2_500, AssertionPredicate.Exceeds, 2_000)]
    public async Task Array_LohClaimReferencesTheThresholdThatSetItsSeverity(long lohMb, AssertionPredicate predicate, long budgetMb)
    {
        Claim claim = (await RunAsync(new ArraySynthesisRule(), [ArrayComposition(lohBytes: lohMb * Mb, lohCount: 5)])).Single();

        claim.Assertion!.Predicate.Should().Be(predicate);
        claim.Assertion.Reference!.Value.Value.Should().BeApproximately(budgetMb * 1_000_000.0, 1);
    }

    [Fact]
    public async Task Array_SparseClaimsAreOnePerArray_CitingThatArraysOwnObservation()
    {
        Observation first = SparseArray("System.Object", 100_000, 0.9, 20 * Mb, 4096);
        Observation second = SparseArray("System.String", 50_000, 0.8, 12 * Mb, 8192);

        IReadOnlyList<Claim> claims = await RunAsync(new ArraySynthesisRule(), [first, second]);

        claims.Should().HaveCount(2);
        claims[0].Support.Should().ContainSingle().Which.Observations.Should().Equal(first.Id);
        claims[1].Support.Should().ContainSingle().Which.Observations.Should().Equal(second.Id);
        claims[0].FalsifiedBy.Should().Contain(test => test.Command == "!da 0x1000");
    }

    // ── GCGeneration ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GCGeneration_ClaimShape_MatchesGolden()
    {
        await RuleShapeGolden.AssertMatchesAsync(new GCGenerationSynthesisRule(),
        [
            new("everything-fires", [Generations(pohCount: 250, lohPercent: 40, gen0Count: 800, gen2Percent: 80), TopAllocator("App.Request", 600), TopAccumulator("App.Cache", 400 * Mb)]),
            new("gen2-warning-bytes-approximate", [Generations(0, 5, 100, 60) with { Confidence = 0.8 }]),
            new("gen2-warning-fallback-mode", [DegradedGenerations(Generations(0, 5, 100, 60))]),
            new("quiet", [Generations(0, 5, 100, 10)]),
        ]);
    }

    [Fact]
    public async Task GCGeneration_Gen2CounterAdmitsWhenTheSplitCannotBeTrusted()
    {
        Claim exact = (await RunAsync(new GCGenerationSynthesisRule(), [Generations(0, 5, 100, 60)])).Single();
        Claim approximate = (await RunAsync(new GCGenerationSynthesisRule(), [Generations(0, 5, 100, 60) with { Confidence = 0.8 }])).Single();
        Claim fallback = (await RunAsync(new GCGenerationSynthesisRule(), [DegradedGenerations(Generations(0, 5, 100, 60))])).Single();

        exact.Counter!.Statements.Should().BeEmpty();
        approximate.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("approximate");
        fallback.Counter!.Statements.Should().ContainSingle().Which.Why.Should().Contain("fallback");
    }

    [Fact]
    public async Task GCGeneration_EachShareClaimComparesToItsOwnThreshold()
    {
        IReadOnlyList<Claim> claims = await RunAsync(new GCGenerationSynthesisRule(), [Generations(250, 40, 800, 80)]);

        claims.Single(c => c.Fingerprint.EndsWith("poh-share")).Assertion!.Reference!.Value.Value.Should().Be(10);
        claims.Single(c => c.Fingerprint.EndsWith("loh-share")).Assertion!.Reference!.Value.Value.Should().Be(20);
        claims.Single(c => c.Fingerprint.EndsWith("gen0-pressure")).Assertion!.Reference!.Value.Value.Should().Be(70);
        claims.Single(c => c.Fingerprint.EndsWith("gen2-pressure")).Assertion!.Reference!.Basis.Should().Be("critical Gen2 threshold");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Claim>> RunAsync(ISynthesisRule rule, IReadOnlyList<Observation> observations) =>
        await rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    private static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    private static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    private static Measure Ratio(double value) => new(value, MeasureUnit.Ratio, MeasureSemantics.Ratio);

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static EvidenceRef Describe(string description) =>
        new() { Artifact = new ArtifactId("rule-shape-golden"), Locator = "0", Description = description };

    private static Observation CrashPopulation(long total, long active, long unique) =>
        RuleShapeGolden.Observation("runtime.crash-population", "Crash Analysis", new()
        {
            ["total.count"] = Count(total),
            ["active.count"] = Count(active),
            ["unique-types.count"] = Count(unique),
        });

    private static Observation CrashCandidate(string type, int active, string tier) =>
        RuleShapeGolden.Observation("runtime.crash-thread-candidate", "Crash Analysis", new() { ["active-exception.count"] = Count(active) }) with
        {
            Subjects = [Type(type)],
            Evidence = [Describe(tier)],
        };

    private static Observation ActiveException(string type) =>
        RuleShapeGolden.Observation("runtime.active-exception", "Crash Analysis", new()) with { Subjects = [Type(type)] };

    private static Observation Reservation(
        double ratio, double ephemeralFill = 40, int pointerSize = 8, long reserved = 6 * Gb, long committed = 2 * Gb) =>
        RuleShapeGolden.Observation("heap.segment-reservation", "Segment Reservation Analysis", new()
        {
            ["committed.bytes"] = Bytes(committed),
            ["reserved.bytes"] = Bytes(reserved),
            ["reservation-gap.bytes"] = Bytes(reserved - committed),
            ["reserved-to-committed.ratio"] = Ratio(ratio),
            ["ephemeral.segment-count"] = Count(2),
            ["ephemeral.avg-fill-percent"] = Percent(ephemeralFill),
            ["dump-pointer-size"] = Count(pointerSize),
            ["logical-heap.count"] = Count(4),
            ["ratio-high-pressure.threshold"] = Ratio(4),
            ["ratio-medium-pressure.threshold"] = Ratio(2),
            ["thirty-two-bit-pressure.threshold-bytes"] = Bytes(2_000_000_000),
            ["near-empty-region-fill-percent.threshold"] = Percent(10),
        });

    private static Observation Region(long nearEmpty, long committed) =>
        RuleShapeGolden.Observation("heap.region-composition", "Segment Reservation Analysis", new()
        {
            ["near-empty.count"] = Count(nearEmpty),
            ["near-empty.committed-bytes"] = Bytes(committed),
        });

    private static Observation DbPopulation(long total, long closed, long broken) =>
        RuleShapeGolden.Observation("infra.db-connection-population", "DB Connection Analysis", new()
        {
            ["total.count"] = Count(total),
            ["closed.count"] = Count(closed),
            ["broken.count"] = Count(broken),
            ["other.count"] = Count(0),
            ["unknown-state.count"] = Count(0),
        });

    private static IEnumerable<Observation> Connections(int generation, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            Observation connection = RuleShapeGolden.Observation("infra.db-connection", "DB Connection Analysis", new()
            {
                ["generation.ordinal"] = Count(generation),
                ["retained.bytes"] = Bytes(1_000 * (i + 1)),
            });
            return generation == 2 && i == count - 1 ? connection with { Evidence = [Describe("static App.Db.Shared")] } : connection;
        });

    private static Observation DbType(string type, long instances) =>
        RuleShapeGolden.Observation("infra.db-connection-type-population", "DB Connection Analysis", new() { ["instance.count"] = Count(instances) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation ArrayComposition(
        long lohBytes = 0, long lohCount = 0, int rank = 1, long instances = 0, long shallowBytes = 0, long pinnedCount = 0, long pinnedBytes = 0) =>
        RuleShapeGolden.Observation("type.array-composition", "Array Analysis", new()
        {
            ["loh.bytes"] = Bytes(lohBytes),
            ["loh.count"] = Count(lohCount),
            ["array.rank"] = Count(rank),
            ["instance.count"] = Count(instances),
            ["shallow.bytes"] = Bytes(shallowBytes),
            ["pinned.count"] = Count(pinnedCount),
            ["pinned.bytes"] = Bytes(pinnedBytes),
        });

    private static Observation LargeArray(string elementType) =>
        RuleShapeGolden.Observation("heap.large-array", "Array Analysis", new()) with { Subjects = [Type(elementType)] };

    private static Observation PinnedArray(string elementType) =>
        RuleShapeGolden.Observation("heap.pinned-array", "Array Analysis", new()) with { Subjects = [Type(elementType)] };

    private static Observation SparseArray(string elementType, int length, double ratio, long wasted, ulong address) =>
        RuleShapeGolden.Observation("heap.sparse-array", "Array Analysis", new()
        {
            ["sparse.ratio"] = Ratio(ratio),
            ["wasted.bytes"] = Bytes(wasted),
            ["length"] = Count(length),
        }) with
        {
            Subjects = [Type(elementType)],
            Evidence = [new EvidenceRef { Artifact = new ArtifactId("rule-shape-golden"), Locator = address.ToString() }],
        };

    private static Observation Generations(long pohCount, double lohPercent, long gen0Count, double gen2Percent) =>
        RuleShapeGolden.Observation("gc.generation-composition", "GC Generation Analysis", new()
        {
            ["total.count"] = Count(1_000),
            ["poh.count"] = Count(pohCount),
            ["poh.bytes"] = Bytes(pohCount > 0 ? 10 * Mb : 0),
            ["poh-pressure.threshold"] = Percent(10),
            ["loh.percent"] = Percent(lohPercent),
            ["loh-pressure.threshold"] = Percent(20),
            ["gen0.count"] = Count(gen0Count),
            ["gen0.bytes"] = Bytes(50 * Mb),
            ["gen0-pressure.threshold"] = Percent(70),
            ["gen2.percent"] = Percent(gen2Percent),
            ["gen2.count"] = Count(500),
            ["gen2.bytes"] = Bytes(500 * Mb),
            ["gen1.bytes"] = Bytes(20 * Mb),
            ["loh.bytes"] = Bytes(100 * Mb),
            ["finalizable-gen2.count"] = Count(0),
            ["finalizable-gen2.bytes"] = Bytes(0),
            ["has-gen2-bytes"] = Count(1),
        });

    private static Observation DegradedGenerations(Observation exact) =>
        exact with { Provenance = exact.Provenance with { Fidelity = FidelityLevel.Degraded } };

    private static Observation TopAllocator(string type, long gen0) =>
        RuleShapeGolden.Observation("gc.generation-top-allocator", "GC Generation Analysis", new() { ["gen0.count"] = Count(gen0) }) with
        {
            Subjects = [Type(type)],
        };

    private static Observation TopAccumulator(string type, long gen2Bytes) =>
        RuleShapeGolden.Observation("gc.generation-top-accumulator", "GC Generation Analysis", new()
        {
            ["gen2.bytes"] = Bytes(gen2Bytes),
            ["gen2.count"] = Count(100),
        }) with { Subjects = [Type(type)] };
}
