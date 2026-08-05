using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class EventLeakSectionBuilderTests
{
    private static EventLeakDomainResult BuildResult(string? rootHint, EventLeakEvidence? evidence = null) =>
        new(
            TotalEventLeakInstances: 1,
            TotalSubscribers: 3,
            StaticEventLeakCount: 1,
            InstanceEventLeakCount: 0,
            TopLeakInstances:
            [
                new EventLeakInstanceSnapshot(
                    PublisherType: "App.MyPublisher",
                    EventFieldName: "MyEvent",
                    IsStatic: true,
                    PublisherAddress: 0,
                    SeverityScore: 90,
                    SubscriberCount: 3,
                    RootHint: rootHint,
                    SubscriberTypes: [new SubscriberTypeCount("App.MySubscriber", 3)],
                    Evidence: evidence)
            ]);

    [Fact]
    public void Build_ShouldTranslateRawRootKind_ToHumanReadableLabel()
    {
        EventLeakDomainResult result = BuildResult("StaticVar");

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.EventLeakInstanceCards.Should().ContainSingle();
        section.EventLeakInstanceCards![0].RootHint.Should().Be("static field (subscriber-derived)");
    }

    [Fact]
    public void Build_ShouldPassThroughUnknownRootKind_Unchanged()
    {
        EventLeakDomainResult result = BuildResult("Unknown(42)");

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.EventLeakInstanceCards![0].RootHint.Should().Be("Unknown(42) (subscriber-derived)");
    }

    [Fact]
    public void Build_ShouldPreferPublisherRootPath_OverTranslatedRootHint()
    {
        EventLeakDomainResult result = BuildResult(
            rootHint: "StaticVar",
            evidence: new EventLeakEvidence(
                SchemaVersion: 1,
                PublisherRootPath: "static App.MyPublisher.MyEvent -> App.MySubscriber",
                SampleSubscriberHint: null,
                SearchTruncated: false,
                Signals: []));

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.EventLeakInstanceCards![0].RootHint.Should().Be("static App.MyPublisher.MyEvent -> App.MySubscriber");
    }
}
