using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="WcfChannelFindingGenerator"/> (old) and
/// <see cref="WcfChannelSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <see cref="BuildResult"/> derives every aggregate from a single list of channel-type entries
/// (total count, faulted count, binding hint, endpoints) — the same "keep fixtures consistent with
/// real production coupling" principle earlier playbook entries use, since the new rule can only
/// reconstruct <c>FaultedChannels</c> and the faulted-type breakdown from the faulted-only stream
/// grouped by type.
/// </remarks>
public sealed class WcfChannelSynthesisRuleParityTests
{
    private readonly WcfChannelFindingGenerator _oldGenerator = new();
    private readonly WcfChannelSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    private sealed record TypeEntry(
        string TypeName, int Total, int Faulted, WcfBindingHint BindingHint = WcfBindingHint.Unknown,
        IReadOnlyList<string?>? FaultedEndpoints = null, bool IsDuplex = false, bool IsSession = false);

    [Fact]
    public Task NoChannelsOrFactories_ReturnsEmpty() => AssertParity([]);

    [Fact]
    public Task FactoryOnly_NoChannels() => AssertParity([], factoryCount: 5);

    [Fact]
    public Task FaultedChannelsPresent() => AssertParity(
        [new TypeEntry("MyApp.Client.IMyServiceChannel", Total: 10, Faulted: 3, BindingHint: WcfBindingHint.NetTcp)]);

    [Fact]
    public Task FaultedChannelsWithEndpoints() => AssertParity(
    [
        new TypeEntry("MyApp.Client.IMyServiceChannel", Total: 10, Faulted: 3, BindingHint: WcfBindingHint.NetTcp,
            FaultedEndpoints: ["net.tcp://server1:808/svc", "net.tcp://server1:808/svc", "net.tcp://server2:808/svc"]),
    ]);

    [Fact]
    public Task FaultedBreakdownTrustsTotalCountOrderNotFaultedCountOrder() => AssertParity(
    [
        new TypeEntry("MyApp.Client.BigChannel", Total: 100, Faulted: 1, BindingHint: WcfBindingHint.Basic),
        new TypeEntry("MyApp.Client.SmallChannel", Total: 5, Faulted: 5, BindingHint: WcfBindingHint.NetTcp),
    ]);

    [Fact]
    public Task FaultedBreakdownSkipsTypesWithZeroFaultedCount() => AssertParity(
    [
        new TypeEntry("MyApp.Client.HealthyChannel", Total: 50, Faulted: 0),
        new TypeEntry("MyApp.Client.FaultyChannel", Total: 10, Faulted: 2, BindingHint: WcfBindingHint.WsHttp),
    ]);

    [Fact]
    public Task FaultedBreakdownTruncatesAtThreeTypes() => AssertParity(
    [
        new TypeEntry("MyApp.Client.TypeA", Total: 40, Faulted: 1),
        new TypeEntry("MyApp.Client.TypeB", Total: 30, Faulted: 1),
        new TypeEntry("MyApp.Client.TypeC", Total: 20, Faulted: 1),
        new TypeEntry("MyApp.Client.TypeD", Total: 10, Faulted: 1),
    ]);

    [Fact]
    public Task NoFaultedChannels_NoFaultedFinding() => AssertParity(
        [new TypeEntry("MyApp.Client.HealthyChannel", Total: 150, Faulted: 0)]);

    [Fact]
    public Task TotalChannelsAtWarningThreshold() => AssertParity(
        [new TypeEntry("MyApp.Client.HealthyChannel", Total: 100, Faulted: 0)]);

    [Fact]
    public Task TotalChannelsAtCriticalThreshold() => AssertParity(
        [new TypeEntry("MyApp.Client.HealthyChannel", Total: 500, Faulted: 0)]);

    [Fact]
    public Task TotalChannelsBelowThreshold_NoChannelCountFinding() => AssertParity(
        [new TypeEntry("MyApp.Client.HealthyChannel", Total: 50, Faulted: 0)]);

    [Fact]
    public Task DuplexAndSessionChannelsCounted() => AssertParity(
    [
        new TypeEntry("MyApp.Client.DuplexChannel", Total: 200, Faulted: 0, IsDuplex: true),
        new TypeEntry("MyApp.Client.SessionChannel", Total: 100, Faulted: 0, IsSession: true),
    ]);

    [Fact]
    public Task ChannelFactoryPresent() => AssertParity(
        [new TypeEntry("MyApp.Client.HealthyChannel", Total: 50, Faulted: 0)], factoryCount: 20);

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(
    [
        new TypeEntry("MyApp.Client.BigChannel", Total: 400, Faulted: 50, BindingHint: WcfBindingHint.NetTcp,
            FaultedEndpoints: ["net.tcp://server1:808/svc"], IsDuplex: true, IsSession: true),
        new TypeEntry("MyApp.Client.OtherChannel", Total: 200, Faulted: 10, BindingHint: WcfBindingHint.Basic),
    ], factoryCount: 15);

    private async Task AssertParity(IReadOnlyList<TypeEntry> types, int factoryCount = 0)
    {
        WcfChannelDomainResult result = BuildResult(types, factoryCount);
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(types, factoryCount, result);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(IReadOnlyList<TypeEntry> types, int factoryCount, WcfChannelDomainResult result)
    {
        if (result.TotalChannels == 0 && factoryCount == 0) return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>();

        // Sorted descending by Total, matching WcfChannelAnalyzer.Analyze's own byType.Sort before
        // its emission loop runs.
        var sortedByTotal = types.OrderByDescending(t => t.Total).ToList();

        foreach (TypeEntry type in sortedByTotal)
        {
            for (int i = 0; i < type.Faulted; i++)
            {
                string? endpoint = type.FaultedEndpoints is not null && i < type.FaultedEndpoints.Count ? type.FaultedEndpoints[i] : null;
                observations.Add(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "infra.wcf-channel",
                    Subjects = [new TypeRef { CanonicalName = type.TypeName, Fidelity = MatchFidelity.Exact }],
                    When = Point(),
                    Measures = new Dictionary<string, Measure>
                    {
                        ["binding-hint.ordinal"] = new Measure((double)(int)type.BindingHint, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    },
                    Provenance = Provenance(capabilitiesUsed),
                    Confidence = 1.0,
                    Evidence = endpoint is not null
                        ? [new EvidenceRef { Artifact = TestArtifact, Locator = "0x1000", Description = endpoint }]
                        : [],
                });
            }

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.wcf-channel-type-population",
                Subjects = [new TypeRef { CanonicalName = type.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(type.Total, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        if (types.Count > 0 || factoryCount > 0)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.wcf-channel-population",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(result.TotalChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["opening.count"] = new Measure(result.OpeningChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["opened.count"] = new Measure(result.OpenedChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["closing.count"] = new Measure(result.ClosingChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["closed.count"] = new Measure(result.ClosedChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["other.count"] = new Measure(result.OtherChannels, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["invalid-state.count"] = new Measure(result.InvalidStateCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["duplex.count"] = new Measure(result.DuplexChannelCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["session.count"] = new Measure(result.SessionChannelCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["factory.count"] = new Measure(result.FactoryCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "WCF Channel Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static WcfChannelDomainResult BuildResult(IReadOnlyList<TypeEntry> types, int factoryCount)
    {
        int total = 0, faulted = 0, duplex = 0, session = 0;
        var byType = new List<WcfChannelTypeSummary>();
        var topFaulted = new List<WcfChannelSnapshot>();

        foreach (TypeEntry type in types)
        {
            total += type.Total;
            faulted += type.Faulted;
            if (type.IsDuplex) duplex += type.Total;
            if (type.IsSession) session += type.Total;
            byType.Add(new WcfChannelTypeSummary(
                type.TypeName, type.Total, OpeningCount: 0, OpenedCount: type.Total - type.Faulted,
                FaultedCount: type.Faulted, ClosingCount: 0, ClosedCount: 0, OtherCount: 0, TotalBytes: 0,
                BindingHint: type.BindingHint));

            for (int i = 0; i < type.Faulted; i++)
            {
                string? endpoint = type.FaultedEndpoints is not null && i < type.FaultedEndpoints.Count ? type.FaultedEndpoints[i] : null;
                topFaulted.Add(new WcfChannelSnapshot(type.TypeName, Address: (ulong)(0x1000 + i), StateLabel: "Faulted", StateValue: 3, RemoteAddress: endpoint));
            }
        }

        byType = byType.OrderByDescending(t => t.TotalCount).ToList();

        return new WcfChannelDomainResult(
            WcfPresent: total > 0 || factoryCount > 0,
            TotalChannels: total,
            OpeningChannels: 0,
            OpenedChannels: total - faulted,
            FaultedChannels: faulted,
            ClosingChannels: 0,
            ClosedChannels: 0,
            OtherChannels: 0,
            ByType: byType,
            TopFaultedChannels: topFaulted,
            FactoryCount: factoryCount,
            TotalBytes: 0,
            InvalidStateCount: 0,
            DuplexChannelCount: duplex,
            SessionChannelCount: session);
    }
}
