using DumpDetective.Analysis.Models;
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
/// Field-for-field parity between <see cref="EventLeakFindingGenerator"/> (old) and
/// <see cref="EventLeakSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// The old generator's <c>ConfidenceScore</c> override on the aggregate findings (via
/// <c>EvidenceConfidence.Compute(topEvidence)</c>) is deliberately not reproduced — same choice
/// <c>TimerLeakSynthesisRule</c>/<c>DominatorSynthesisRule</c> made, since no rendered text reads it.
/// </remarks>
public sealed class EventLeakSynthesisRuleParityTests
{
    private readonly EventLeakFindingGenerator _oldGenerator = new();
    private readonly EventLeakSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoLeaks_ReturnsInfoFinding() => AssertParity(BuildResult());

    [Fact]
    public Task MixedLeakGroups_EmitsOneInstanceAndOneStaticFinding() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 30, 12, 140, 11.6, 1, 20),
        new EventLeakGroupSnapshot("OrderPublisher", "Closed", false, 18, 7, 30, 4.2, 1, 8),
        new EventLeakGroupSnapshot("GlobalBus", "MessageArrived", true, 26, 1, 95, 95, 95, 95)));

    [Fact]
    public Task OnlyInstanceLeakGroups_EmitsSingleInstanceAggregate() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 22, 8, 64, 8.0, 1, 12),
        new EventLeakGroupSnapshot("CartPublisher", "Updated", false, 19, 5, 28, 5.6, 1, 9)));

    [Fact]
    public Task UsesHighestGroupSeverity_ForAggregateSeverity() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 19, 8, 44, 5.5, 1, 9),
        new EventLeakGroupSnapshot("OrderPublisher", "Completed", false, 41, 4, 60, 15, 1, 30)));

    [Fact]
    public Task TimerEventGroup_TagsFindingAndAddsTimerRecommendation() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("System.Timers.Timer", "Elapsed", false, 22, 8, 64, 8.0, 1, 12,
            IsTimerEvent: true)));

    [Fact]
    public Task PropertyChangedEventGroup_TagsFindingAndAddsPropertyChangedRecommendation() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("App.MyViewModel", "PropertyChanged", false, 22, 8, 64, 8.0, 1, 12,
            IsPropertyChangedEvent: true)));

    [Fact]
    public Task ModerateSeverityScore_EmitsWarningFinding() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, MaxSeverityScore: 22, InstanceCount: 8, TotalSubscribers: 64, AverageSubscribers: 8.0, MinSubscribers: 1, MaxSubscribers: 12)));

    [Fact]
    public Task LowSeverityScore_EmitsInfoFinding() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, MaxSeverityScore: 10, InstanceCount: 8, TotalSubscribers: 64, AverageSubscribers: 8.0, MinSubscribers: 1, MaxSubscribers: 12)));

    [Fact]
    public Task NoTimerOrPropertyChangedGroups_DoesNotAddCategoryTags() => AssertParity(BuildResult(
        new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 22, 8, 64, 8.0, 1, 12)));

    [Fact]
    public Task GroupCountFallback_UsedWhenNoGroupsOfKindSampled() => AssertParity(BuildResultWithFallback(
        instanceLeakCount: 3, staticLeakCount: 0,
        groups: [new EventLeakGroupSnapshot("GlobalBus", "MessageArrived", true, 26, 1, 95, 95, 95, 95)]));

    private static EventLeakDomainResult BuildResult(params EventLeakGroupSnapshot[] groups)
    {
        int totalSubscribers = 0;
        int staticCount = 0;
        int instanceCount = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            totalSubscribers += groups[i].TotalSubscribers;
            if (groups[i].IsStatic) staticCount++;
            else instanceCount++;
        }

        return new EventLeakDomainResult(
            TotalEventLeakInstances: groups.Length,
            TotalSubscribers: totalSubscribers,
            StaticEventLeakCount: staticCount,
            InstanceEventLeakCount: instanceCount,
            TopPublisherEventsBySubscribers: [],
            TopLeakGroups: groups,
            TopLeakInstances: []);
    }

    /// <summary>Exercises the fallback path where TotalEventLeakInstances/InstanceEventLeakCount
    /// disagree with what TopLeakGroups actually samples for a given IsStatic value — e.g. an
    /// instance leak was detected but didn't make the (in real production, unbounded but here
    /// deliberately mismatched for the test) group sample.</summary>
    private static EventLeakDomainResult BuildResultWithFallback(
        int instanceLeakCount, int staticLeakCount, IReadOnlyList<EventLeakGroupSnapshot> groups)
    {
        int totalSubscribers = 0;
        for (int i = 0; i < groups.Count; i++)
            totalSubscribers += groups[i].TotalSubscribers;

        return new EventLeakDomainResult(
            TotalEventLeakInstances: instanceLeakCount + staticLeakCount,
            TotalSubscribers: totalSubscribers,
            StaticEventLeakCount: staticLeakCount,
            InstanceEventLeakCount: instanceLeakCount,
            TopPublisherEventsBySubscribers: [],
            TopLeakGroups: groups,
            TopLeakInstances: []);
    }

    private async Task AssertParity(EventLeakDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
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

    private static IReadOnlyList<Observation> ToObservations(EventLeakDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>();

        foreach (EventLeakGroupSnapshot group in result.TopLeakGroups ?? [])
        {
            string canonicalName = group.PublisherType;

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "event.publisher-leak-group",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["is-static"] = new Measure(group.IsStatic ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["instance.count"] = new Measure(group.InstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["subscriber.count"] = new Measure(group.TotalSubscribers, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["max-subscribers-per-instance.count"] = new Measure(group.MaxSubscribers, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["max-severity.score"] = new Measure(group.MaxSeverityScore, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["is-timer-event"] = new Measure(group.IsTimerEvent ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["is-property-changed-event"] = new Measure(group.IsPropertyChangedEvent ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence = [new EvidenceRef
                {
                    Artifact = TestArtifact,
                    Locator = $"{canonicalName}.{group.EventFieldName}",
                    Description = group.EventFieldName,
                }],
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "event.publisher-leak-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(result.TotalEventLeakInstances, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["instance-leak.count"] = new Measure(result.InstanceEventLeakCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["static-leak.count"] = new Measure(result.StaticEventLeakCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Event Leak Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
