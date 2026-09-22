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
/// Covers the first 7 <c>*InsightRule</c> classes migrated out of <c>InsightEngine</c>'s
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
        IReadOnlyList<EntityRef>? subjects = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjects ?? [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

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

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }
}
