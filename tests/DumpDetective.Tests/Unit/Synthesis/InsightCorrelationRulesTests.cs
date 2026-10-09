using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Covers the first 14 <c>*InsightRule</c> classes migrated out of <c>InsightEngine</c>'s
/// <c>CorrelationRuleGroup</c> (phase 5 step 6.2,
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md). See
/// <c>InsightBaselineRulesTests</c>/<c>InsightMemoryAndRuntimeRulesTests</c> for the other two
/// groups' coverage.
/// </summary>
public sealed class InsightCorrelationRulesTests
{
    private static Observation Obs(
        string observationType,
        IReadOnlyDictionary<string, Measure> measures,
        IReadOnlyList<EntityRef>? subjects = null,
        IReadOnlyList<EvidenceRef>? evidence = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjects ?? [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Evidence = evidence ?? [],
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static EvidenceRef Ev(string description) => new()
    {
        Artifact = new ArtifactId("test-artifact"),
        Locator = "test",
        Description = description,
    };

    private static Observation ExceptionType(string typeName, int count) => Obs(
        "runtime.exception-type",
        new Dictionary<string, Measure> { ["instance.count"] = new(count, MeasureUnit.Count, MeasureSemantics.Absolute) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    private static Measure Count(double v) => new(v, MeasureUnit.Count, MeasureSemantics.Absolute);
    private static Measure Bytes(double v) => new(v, MeasureUnit.Bytes, MeasureSemantics.Absolute);
    private static Measure Pct(double v) => new(v, MeasureUnit.Percent, MeasureSemantics.Ratio);

    public sealed class FatalExceptionOnHeap
    {
        private readonly FatalExceptionOnHeapInsightRule _rule = new();

        [Fact]
        public async Task FatalTypePresent_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.crash-population", new Dictionary<string, Measure> { ["total.count"] = Count(3), ["active.count"] = Count(0), ["unique-types.count"] = Count(2) }),
                ExceptionType("System.OutOfMemoryException", 1),
                ExceptionType("System.InvalidOperationException", 2),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Title.Should().Contain("Fatal exception");
        }

        [Fact]
        public async Task NoFatalType_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.crash-population", new Dictionary<string, Measure> { ["total.count"] = Count(2), ["active.count"] = Count(0), ["unique-types.count"] = Count(1) }),
                ExceptionType("System.InvalidOperationException", 2),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class EventLeakPattern
    {
        private readonly EventLeakPatternInsightRule _rule = new();

        [Fact]
        public async Task HighGen2Correlation_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("event.publisher-leak-population", new Dictionary<string, Measure> { ["total.count"] = Count(5), ["instance-leak.count"] = Count(3), ["static-leak.count"] = Count(2), ["subscriber.count"] = Count(200) }),
                Obs("gc.generation-composition", new Dictionary<string, Measure>
                {
                    ["gen0.bytes"] = Bytes(0), ["gen1.bytes"] = Bytes(0), ["gen2.bytes"] = Bytes(0), ["loh.bytes"] = Bytes(0), ["poh.bytes"] = Bytes(0),
                    ["gen2.fraction"] = new Measure(0.5, MeasureUnit.Ratio, MeasureSemantics.Ratio), ["loh.fraction"] = new Measure(0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen0.count"] = Count(0), ["gen1.count"] = Count(0), ["gen2.count"] = Count(0), ["loh.count"] = Count(0), ["poh.count"] = Count(0), ["total.count"] = Count(100),
                    ["loh.percent"] = Pct(0), ["gen0.percent"] = Pct(0), ["gen1.percent"] = Pct(0), ["gen2.percent"] = Pct(50), ["poh.percent"] = Pct(0),
                }),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].MetricValue.Should().Be(200);
        }

        [Fact]
        public async Task NoCorrelatingSignal_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("event.publisher-leak-population", new Dictionary<string, Measure> { ["total.count"] = Count(5), ["instance-leak.count"] = Count(3), ["static-leak.count"] = Count(2), ["subscriber.count"] = Count(200) }),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class RecurringTimeoutPattern
    {
        private readonly RecurringTimeoutPatternInsightRule _rule = new();

        [Fact]
        public async Task ManyTimeouts_EscalatesToWarning()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.crash-population", new Dictionary<string, Measure> { ["total.count"] = Count(25), ["active.count"] = Count(0), ["unique-types.count"] = Count(1) }),
                ExceptionType("System.TimeoutException", 25),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
        }

        [Fact]
        public async Task FewTimeouts_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.crash-population", new Dictionary<string, Measure> { ["total.count"] = Count(3), ["active.count"] = Count(0), ["unique-types.count"] = Count(1) }),
                ExceptionType("System.TimeoutException", 3),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class DbConnectionLeak
    {
        private readonly DbConnectionLeakInsightRule _rule = new();

        [Fact]
        public async Task ManyOpenConnectionsWithTimeouts_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.db-connection-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(20), ["open.count"] = Count(15), ["closed.count"] = Count(5), ["broken.count"] = Count(0),
                    ["other.count"] = Count(0), ["unknown-state.count"] = Count(0), ["gen2-open.count"] = Count(0), ["gen0-open.count"] = Count(0),
                }),
                ExceptionType("System.TimeoutException", 8),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
        }
    }

    public sealed class WcfChannelFault
    {
        private readonly WcfChannelFaultInsightRule _rule = new();

        [Fact]
        public async Task FaultedChannelsWithCommunicationExceptions_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.wcf-channel-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(5), ["opening.count"] = Count(0), ["opened.count"] = Count(1), ["faulted.count"] = Count(4),
                    ["closing.count"] = Count(0), ["closed.count"] = Count(0), ["other.count"] = Count(0), ["invalid-state.count"] = Count(0),
                    ["duplex.count"] = Count(0), ["session.count"] = Count(0), ["factory.count"] = Count(0),
                }),
                ExceptionType("System.ServiceModel.CommunicationObjectFaultedException", 3),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }
    }

    public sealed class WcfOpeningTimeoutCorrelation
    {
        private readonly WcfOpeningTimeoutCorrelationInsightRule _rule = new();

        [Fact]
        public async Task OpeningChannelsWithTimeouts_EscalatesWithHighCount()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.wcf-channel-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(5), ["opening.count"] = Count(5), ["opened.count"] = Count(0), ["faulted.count"] = Count(0),
                    ["closing.count"] = Count(0), ["closed.count"] = Count(0), ["other.count"] = Count(0), ["invalid-state.count"] = Count(0),
                    ["duplex.count"] = Count(0), ["session.count"] = Count(0), ["factory.count"] = Count(0),
                }),
                ExceptionType("System.TimeoutException", 1),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
        }

        [Fact]
        public async Task NoOpeningChannels_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.wcf-channel-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(1), ["opening.count"] = Count(0), ["opened.count"] = Count(1), ["faulted.count"] = Count(0),
                    ["closing.count"] = Count(0), ["closed.count"] = Count(0), ["other.count"] = Count(0), ["invalid-state.count"] = Count(0),
                    ["duplex.count"] = Count(0), ["session.count"] = Count(0), ["factory.count"] = Count(0),
                }),
                ExceptionType("System.TimeoutException", 1),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class HttpClientAccumulation
    {
        private readonly HttpClientAccumulationInsightRule _rule = new();

        [Fact]
        public async Task MixedApiUsage_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.http-object-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(20), ["http-client.count"] = Count(5), ["http-web-request.count"] = Count(0),
                    ["http-web-response.count"] = Count(12), ["http-message-handler.count"] = Count(0), ["service-point.count"] = Count(0),
                    ["active-handler-tracking-entry.count"] = Count(0), ["expired-handler-tracking-entry.count"] = Count(0),
                    ["http-client-gen0.count"] = Count(0), ["http-client-gen1.count"] = Count(0), ["http-client-gen2.count"] = Count(0),
                }),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }

        [Fact]
        public async Task HttpClientOnly_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("infra.http-object-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(5), ["http-client.count"] = Count(5), ["http-web-request.count"] = Count(0),
                    ["http-web-response.count"] = Count(0), ["http-message-handler.count"] = Count(0), ["service-point.count"] = Count(0),
                    ["active-handler-tracking-entry.count"] = Count(0), ["expired-handler-tracking-entry.count"] = Count(0),
                    ["http-client-gen0.count"] = Count(0), ["http-client-gen1.count"] = Count(0), ["http-client-gen2.count"] = Count(0),
                }),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static Observation FinalizablePopulation(string typeName, long gen2Count) => Obs(
        "type.finalizable-population",
        new Dictionary<string, Measure> { ["instance.count"] = Count(gen2Count), ["shallow.bytes"] = Bytes(0), ["gen2.count"] = Count(gen2Count) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    private static Observation QueueEntry(string typeName) => Obs(
        "finalizer.queue-entry",
        new Dictionary<string, Measure> { ["retained.bytes"] = Bytes(0), ["shallow.bytes"] = Bytes(0), ["disposed-but-live"] = Count(0), ["is-critical-finalizer"] = Count(0) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    private static Observation KnownPattern(string typeName, long count) => Obs(
        "type.known-pattern-population",
        new Dictionary<string, Measure> { ["instance.count"] = Count(count), ["shallow.bytes"] = Bytes(0) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    public sealed class DataTableLifecycle
    {
        private readonly DataTableLifecycleInsightRule _rule = new();

        [Fact]
        public async Task DataColumnFinalizerOverThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                FinalizablePopulation("System.Data.DataColumn", gen2Count: 150),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("DataColumn ×150");
        }

        [Fact]
        public async Task QueueEntriesAloneCrossThreshold_Fires()
        {
            var observations = new List<Observation>();
            for (int i = 0; i < 120; i++)
                observations.Add(QueueEntry("System.Data.DataColumn"));

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("DataColumn ×120");
        }

        [Fact]
        public async Task DataRowHeapOverThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                KnownPattern("System.Data.DataRow", count: 15_000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("DataRow ×15,000");
        }

        [Theory]
        [InlineData("System.Data.DataRowView")]
        [InlineData("System.Data.DataRowCollection")]
        [InlineData("System.Linq.Enumerable+<CastIterator>d__<System.Data.DataRow>")]
        public async Task DataRowHeap_CountsOnlyTheRealDataRowType(string lookalike)
        {
            var matched = new ObservationMatchSet([KnownPattern(lookalike, count: 15_000)]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task BelowBothThresholds_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                FinalizablePopulation("System.Data.DataColumn", gen2Count: 10),
                KnownPattern("System.Data.DataRow", count: 500),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class KnownLeakPatterns
    {
        private readonly KnownLeakPatternsInsightRule _rule = new();

        [Fact]
        public async Task TdsParserClosureOverThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass45_0", count: 1_500),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Title.Should().Contain("TdsParser");
        }

        [Fact]
        public async Task ReflectionMetadataOverThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                KnownPattern("System.Reflection.RuntimeMethodInfo", count: 40_000),
                KnownPattern("System.Reflection.RuntimePropertyInfo", count: 20_000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Title.Should().Contain("reflection");
        }

        [Fact]
        public async Task BothPatternsOverThreshold_FiresBoth()
        {
            var matched = new ObservationMatchSet(
            [
                KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass45_0", count: 1_500),
                KnownPattern("System.Reflection.RuntimeMethodInfo", count: 60_000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().HaveCount(2);
        }

        [Fact]
        public async Task BelowThresholds_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                KnownPattern("System.Data.SqlClient.TdsParser+<>c__DisplayClass45_0", count: 10),
                KnownPattern("System.Reflection.RuntimeMethodInfo", count: 10),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static Observation PinnedBytes(string typeName, double bytes) => Obs(
        "handle.pinned-target-bytes",
        new Dictionary<string, Measure> { ["retained.bytes"] = Bytes(bytes) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    private static Observation PinnedTargetType(string typeName, int count) => Obs(
        "handle.pinned-target-type",
        new Dictionary<string, Measure> { ["instance.count"] = Count(count) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

    public sealed class PinnedStringLeak
    {
        private readonly PinnedStringLeakInsightRule _rule = new();

        [Fact]
        public async Task BytesAboveMinThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                PinnedBytes("System.String", 2 * 1024 * 1024),
                PinnedTargetType("System.String", count: 500),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Narrative.Should().Contain("500");
        }

        [Fact]
        public async Task SyncAndAsyncBytesAreSummed_CriticalAboveThreshold()
        {
            var matched = new ObservationMatchSet(
            [
                PinnedBytes("System.String", 15 * 1024 * 1024),
                PinnedBytes("System.String", 10 * 1024 * 1024),
                PinnedTargetType("System.String", count: 900),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
        }

        [Fact]
        public async Task NonStringTypeIgnored()
        {
            var matched = new ObservationMatchSet(
            [
                PinnedBytes("System.Byte[]", 50 * 1024 * 1024),
                PinnedTargetType("System.Byte[]", count: 900),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task BelowMinThreshold_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                PinnedBytes("System.String", 100 * 1024),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static Observation Cluster(int rank, int count, string signature) => Obs(
        "thread.stack-cluster",
        new Dictionary<string, Measure>
        {
            ["cluster.count"] = Count(count),
            ["threadpool-worker.count"] = Count(0),
            ["gc.count"] = Count(0),
            ["finalizer.count"] = Count(0),
            ["cluster.rank"] = Count(rank),
        },
        evidence: [Ev(signature)]);

    private static Observation ClusterMember(uint osThreadId, int rank) => Obs(
        "thread.stack-cluster-member",
        new Dictionary<string, Measure> { ["cluster.rank"] = Count(rank) },
        subjects: [new ThreadRef { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact }]);

    private static Observation Diversity(int aliveThreadCount) => Obs(
        "runtime.thread-cluster-diversity",
        new Dictionary<string, Measure>
        {
            ["alive-thread.count"] = Count(aliveThreadCount),
            ["unique-cluster.count"] = Count(0),
            ["diversity.percent"] = Pct(0),
        });

    private static Observation WaitState(uint osThreadId, int waitTypeOrdinal, int lockCount = 0) => Obs(
        "thread.wait-state",
        new Dictionary<string, Measure>
        {
            ["locks-held.count"] = Count(lockCount),
            ["circular-wait-candidate.count"] = Count(0),
            ["wait-type.ordinal"] = Count(waitTypeOrdinal),
        },
        subjects: [new ThreadRef { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact }]);

    private static Observation Blocked(uint osThreadId, int locksHeldCount, string? lockTypesCsv = null) => Obs(
        "thread.blocked",
        new Dictionary<string, Measure> { ["locks-held.count"] = Count(locksHeldCount) },
        subjects: [new ThreadRef { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact }],
        evidence: lockTypesCsv is null ? [] : [Ev(lockTypesCsv)]);

    private static Observation ThreadState(uint osThreadId, int stackRootCount) => Obs(
        "thread.state",
        new Dictionary<string, Measure> { ["stack-root.count"] = Count(stackRootCount) },
        subjects: [new ThreadRef { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact }]);

    public sealed class ClusterHangCorrelation
    {
        private readonly ClusterHangCorrelationInsightRule _rule = new();

        [Fact]
        public async Task OverlapAboveThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 10, signature: "MonitorAcquire -> Foo.Bar"),
                ClusterMember(1, 0), ClusterMember(2, 0), ClusterMember(3, 0), ClusterMember(4, 0), ClusterMember(5, 0),
                Diversity(aliveThreadCount: 100),
                WaitState(1, waitTypeOrdinal: 0), WaitState(2, waitTypeOrdinal: 0),
                WaitState(3, waitTypeOrdinal: 0), WaitState(4, waitTypeOrdinal: 0),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Narrative.Should().Contain("4 of 5 sampled threads");
            findings[0].Narrative.Should().Contain("predominantly waiting on Waiting to acquire monitor lock");
            findings[0].Narrative.Should().Contain("MonitorAcquire -> Foo.Bar");
        }

        [Fact]
        public async Task OverlapBelowThreshold_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 10, signature: "sig"),
                ClusterMember(1, 0), ClusterMember(2, 0), ClusterMember(3, 0), ClusterMember(4, 0), ClusterMember(5, 0),
                Diversity(aliveThreadCount: 20),
                WaitState(1, waitTypeOrdinal: 0),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task DominantPercentAboveHalf_IsCriticalEvenWithoutDeadlockCandidate()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 60, signature: "sig"),
                ClusterMember(1, 0), ClusterMember(2, 0), ClusterMember(3, 0), ClusterMember(4, 0), ClusterMember(5, 0),
                Diversity(aliveThreadCount: 100),
                WaitState(1, waitTypeOrdinal: 1), WaitState(2, waitTypeOrdinal: 1),
                WaitState(3, waitTypeOrdinal: 1), WaitState(4, waitTypeOrdinal: 1), WaitState(5, waitTypeOrdinal: 1),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
        }

        [Fact]
        public async Task OverlappingDeadlockCandidate_EscalatesToCriticalAndNamesLockTypes()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 10, signature: "sig"),
                ClusterMember(1, 0), ClusterMember(2, 0), ClusterMember(3, 0), ClusterMember(4, 0), ClusterMember(5, 0),
                Diversity(aliveThreadCount: 100),
                WaitState(1, waitTypeOrdinal: 0), WaitState(2, waitTypeOrdinal: 0),
                WaitState(3, waitTypeOrdinal: 0), WaitState(4, waitTypeOrdinal: 0),
                Blocked(2, locksHeldCount: 1, lockTypesCsv: "System.Threading.Monitor"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Tags.Should().Contain("deadlock-candidate");
            findings[0].Narrative.Should().Contain("System.Threading.Monitor");
        }

        [Fact]
        public async Task StackRootsOnOverlappingThreads_AddsRetentionNote()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 10, signature: "sig"),
                ClusterMember(1, 0), ClusterMember(2, 0), ClusterMember(3, 0), ClusterMember(4, 0), ClusterMember(5, 0),
                Diversity(aliveThreadCount: 100),
                WaitState(1, waitTypeOrdinal: 0), WaitState(2, waitTypeOrdinal: 0),
                WaitState(3, waitTypeOrdinal: 0), WaitState(4, waitTypeOrdinal: 0),
                ThreadState(1, stackRootCount: 5), ThreadState(2, stackRootCount: 3),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("8 GC root(s)");
        }

        [Fact]
        public async Task NoWaitingThreads_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Cluster(rank: 0, count: 10, signature: "sig"),
                ClusterMember(1, 0),
                Diversity(aliveThreadCount: 20),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static Observation RetainedType(string typeName, int count, string rootDescription) => Obs(
        "heap.static-root-retained-type",
        new Dictionary<string, Measure> { ["instance.count"] = Count(count), ["shallow.bytes"] = Bytes(0) },
        subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        evidence: [Ev(rootDescription)]);

    public sealed class StaticRootFinalizableCorrelation
    {
        private readonly StaticRootFinalizableCorrelationInsightRule _rule = new();

        [Fact]
        public async Task RetainedTypeMatchesQueuedType_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                RetainedType("MyApp.Resource", count: 50, rootDescription: "MyApp.Cache._entries"),
                QueueEntry("MyApp.Resource"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("MyApp.Cache._entries");
            findings[0].Narrative.Should().Contain("MyApp.Resource");
        }

        [Fact]
        public async Task NoRetainedTypeMatchesQueuedType_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                RetainedType("MyApp.Resource", count: 50, rootDescription: "MyApp.Cache._entries"),
                QueueEntry("MyApp.OtherType"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task MultipleRoots_PicksHighestQueueCount()
        {
            var matched = new ObservationMatchSet(
            [
                RetainedType("MyApp.Small", count: 5, rootDescription: "MyApp.SmallCache._entries"),
                QueueEntry("MyApp.Small"),
                RetainedType("MyApp.Big", count: 50, rootDescription: "MyApp.BigCache._entries"),
                QueueEntry("MyApp.Big"), QueueEntry("MyApp.Big"), QueueEntry("MyApp.Big"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("MyApp.BigCache._entries");
            findings[0].Narrative.Should().Contain("3");
        }

        [Fact]
        public async Task NoQueueEntries_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                RetainedType("MyApp.Resource", count: 50, rootDescription: "MyApp.Cache._entries"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static readonly ArtifactId TestArtifact = new("test-artifact");

    private static Observation SqlTransaction(ulong? connectionAddress) => Obs(
        "infra.sql-transaction",
        new Dictionary<string, Measure> { ["shallow.bytes"] = Bytes(0) },
        subjects: connectionAddress is ulong addr
            ? [new TypeRef { CanonicalName = "System.Data.SqlClient.SqlTransaction", Fidelity = MatchFidelity.Exact },
               new ObjectRef { Address = addr, Artifact = TestArtifact, Fidelity = MatchFidelity.Exact }]
            : [new TypeRef { CanonicalName = "System.Data.SqlClient.SqlTransaction", Fidelity = MatchFidelity.Exact }]);

    private static Observation DbConnection(ulong address) => Obs(
        "infra.db-connection",
        new Dictionary<string, Measure> { ["generation.ordinal"] = Count(2), ["retained.bytes"] = Bytes(0) },
        subjects:
        [
            new TypeRef { CanonicalName = "System.Data.SqlClient.SqlConnection", Fidelity = MatchFidelity.Exact },
            new ObjectRef { Address = address, Artifact = TestArtifact, Fidelity = MatchFidelity.Exact },
        ]);

    public sealed class LongHeldTransactionOnOpenConnection
    {
        private readonly LongHeldTransactionOnOpenConnectionInsightRule _rule = new();

        [Fact]
        public async Task CorrelatedCountAtThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                DbConnection(0x1000), DbConnection(0x2000), DbConnection(0x3000), DbConnection(0x4000),
                SqlTransaction(0x1000), SqlTransaction(0x2000), SqlTransaction(0x3000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Title.Should().Contain("3");
        }

        [Fact]
        public async Task CorrelatedCountAtCriticalThreshold_IsCritical()
        {
            var observations = new List<Observation>();
            for (int i = 0; i < 15; i++)
            {
                ulong address = (ulong)(0x1000 * (i + 1));
                observations.Add(DbConnection(address));
                observations.Add(SqlTransaction(address));
            }

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(new ObservationMatchSet(observations), new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
        }

        [Fact]
        public async Task BelowThreshold_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                DbConnection(0x1000), DbConnection(0x2000),
                SqlTransaction(0x1000), SqlTransaction(0x2000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task TransactionsWithNoConnectionAddress_AreIgnored()
        {
            var matched = new ObservationMatchSet(
            [
                DbConnection(0x1000), DbConnection(0x2000), DbConnection(0x3000),
                SqlTransaction(null), SqlTransaction(null), SqlTransaction(null),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task NonMatchingAddresses_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                DbConnection(0x1000), DbConnection(0x2000), DbConnection(0x3000),
                SqlTransaction(0x9000), SqlTransaction(0x9001), SqlTransaction(0x9002),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    private static Observation CrashCandidateModule(string moduleName, int activeExceptionCount) => Obs(
        "runtime.crash-thread-candidate-module",
        new Dictionary<string, Measure> { ["active-exception.count"] = Count(activeExceptionCount) },
        subjects: [new ModuleRef { SimpleName = moduleName, Fidelity = MatchFidelity.Exact }]);

    private static Observation ModuleConflict(string moduleName) => Obs(
        "runtime.module-conflict-group",
        new Dictionary<string, Measure>(),
        subjects: [new ModuleRef { SimpleName = moduleName, Fidelity = MatchFidelity.Exact }]);

    private static Observation ModuleDiskFootprint(string moduleName, ulong bytes) => Obs(
        "runtime.module-disk-footprint",
        new Dictionary<string, Measure> { ["disk.bytes"] = Bytes(bytes) },
        subjects: [new ModuleRef { SimpleName = moduleName, Fidelity = MatchFidelity.Exact }]);

    public sealed class CrashModuleHotspot
    {
        private readonly CrashModuleHotspotInsightRule _rule = new();

        [Fact]
        public async Task DominantModuleWithConflict_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                CrashCandidateModule("MyApp.Data.dll", 4), CrashCandidateModule("System.Private.CoreLib.dll", 1),
                ModuleConflict("MyApp.Data.dll"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Narrative.Should().Contain("MyApp.Data.dll");
            findings[0].Narrative.Should().Contain("conflicting assembly version");
        }

        [Fact]
        public async Task DominantModuleWithDiskSize_NoConflict_FiresAsInfo()
        {
            var matched = new ObservationMatchSet(
            [
                CrashCandidateModule("MyApp.Data.dll", 4), CrashCandidateModule("System.Private.CoreLib.dll", 1),
                ModuleDiskFootprint("MyApp.Data.dll", 2 * 1024 * 1024),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Info);
            findings[0].Narrative.Should().Contain("on disk");
        }

        [Fact]
        public async Task NoCrossAnalyzerSignal_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                CrashCandidateModule("MyApp.Data.dll", 4), CrashCandidateModule("System.Private.CoreLib.dll", 1),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task BelowMinActiveExceptions_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                CrashCandidateModule("MyApp.Data.dll", 1),
                ModuleConflict("MyApp.Data.dll"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task NotDominantShare_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                CrashCandidateModule("MyApp.Data.dll", 2), CrashCandidateModule("System.Private.CoreLib.dll", 5),
                ModuleConflict("MyApp.Data.dll"),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class MemoryTypeGeneration
    {
        private readonly MemoryTypeGenerationInsightRule _rule = new();

        private static Observation TypeProfile(string typeName, double totalBytes, double gen2Bytes,
            double gen0 = 0, double gen1 = 0, double gen2 = 0, double loh = 0) => Obs(
            "gc.generation-type-profile",
            new Dictionary<string, Measure>
            {
                ["total.bytes"] = Bytes(totalBytes), ["gen2.bytes"] = Bytes(gen2Bytes),
                ["gen0.count"] = Count(gen0), ["gen1.count"] = Count(gen1),
                ["gen2.count"] = Count(gen2), ["loh.count"] = Count(loh),
            },
            subjects: [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }]);

        [Fact]
        public async Task LargeTypeAlmostEntirelyGen2_FiresWarningNamingLargest()
        {
            var matched = new ObservationMatchSet(
            [
                TypeProfile("MyApp.Small", 20_000_000, 19_000_000),
                TypeProfile("MyApp.Cache.Entry", 150_000_000, 140_000_000),
            ]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Narrative.Should().Contain("2 type(s)").And.Contain("MyApp.Cache.Entry");
            findings[0].DerivedFrom.Should().HaveCount(2);
        }

        [Fact]
        public async Task LargestMatchUnder100Mb_IsInfo()
        {
            var matched = new ObservationMatchSet([TypeProfile("MyApp.Small", 20_000_000, 19_000_000)]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle().Which.Severity.Should().Be(Severity.Info);
        }

        [Fact]
        public async Task MostlyGen0_DoesNotFire()
        {
            var matched = new ObservationMatchSet([TypeProfile("MyApp.Transient", 150_000_000, 10_000_000)]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task ByteFractionPreferredOverCount_WhenGen2BytesAvailable()
        {
            // Mostly Gen0 by count (50 of 1,000 in Gen2), but those survivors hold 90% of the bytes.
            var matched = new ObservationMatchSet(
                [TypeProfile("MyApp.GrowingList", 150_000_000, 135_000_000, gen0: 950, gen2: 50)]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }

        [Fact]
        public async Task CountFractionUsed_WhenGen2BytesUnavailable()
        {
            var matched = new ObservationMatchSet(
                [TypeProfile("MyApp.Cache.Entry", 150_000_000, 0, gen0: 10, gen1: 40, gen2: 950)]);

            IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }
    }
}
