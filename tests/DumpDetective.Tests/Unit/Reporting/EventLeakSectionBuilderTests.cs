using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
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

    // P1-2 regression (docs/analysis/phase1/eventleak-analyzer-audit.md): SubscriberDetail.SizeIsExact
    // used to be computed by the analyzer and then discarded — SubscriberDetailEntry had no field
    // for it, so exact (dominator-tree) vs. estimated (type-average) subscriber sizes were
    // indistinguishable in the report.
    [Fact]
    public void Build_ShouldPropagateSizeIsExact_ToSubscriberDetailEntry()
    {
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 1,
            TotalSubscribers: 2,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: 1,
            TopLeakInstances:
            [
                new EventLeakInstanceSnapshot(
                    PublisherType: "App.MyPublisher",
                    EventFieldName: "MyEvent",
                    IsStatic: false,
                    PublisherAddress: 0x1000,
                    SeverityScore: 10,
                    SubscriberCount: 2,
                    RootHint: null,
                    SubscriberDetails:
                    [
                        new SubscriberDetail("App.ExactSubscriber", "OnMyEvent", Size: 128, Count: 1, SizeIsExact: true),
                        new SubscriberDetail("App.EstimatedSubscriber", "OnMyEvent", Size: 64, Count: 1, SizeIsExact: false)
                    ])
            ]);

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        var details = section.EventLeakInstanceCards![0].SubscriberDetails!;
        details.Should().Contain(d => d.Type == "App.ExactSubscriber" && d.SizeIsExact);
        details.Should().Contain(d => d.Type == "App.EstimatedSubscriber" && !d.SizeIsExact);
    }

    // P1-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): the section builder used to
    // silently truncate to the top 10 groups/instances with no display cap at all — this project
    // deliberately does not truncate already-fully-computed report data (display caps are
    // distinct from the analyzer's own work-scoping caps, e.g. EventLeakOptions.TopDetailedInstancesPerGroup,
    // which bound cost during the scan itself, not what gets shown afterward).
    [Fact]
    public void Build_ShouldRenderEveryGroupAndInstance_NoDisplayCap()
    {
        const int count = 15; // more than the old MaxGroupsToShow/MaxInstancesToShow of 10

        var groups = Enumerable.Range(0, count)
            .Select(i => new EventLeakGroupSnapshot(
                PublisherType: $"App.Publisher{i}",
                EventFieldName: "MyEvent",
                IsStatic: false,
                SeverityScore: i,
                InstanceCount: 1,
                TotalSubscribers: 1,
                AverageSubscribers: 1,
                MinSubscribers: 1,
                MaxSubscribers: 1))
            .ToList();

        var instances = Enumerable.Range(0, count)
            .Select(i => new EventLeakInstanceSnapshot(
                PublisherType: $"App.Publisher{i}",
                EventFieldName: "MyEvent",
                IsStatic: false,
                PublisherAddress: (ulong)(0x1000 + i),
                SeverityScore: i,
                SubscriberCount: 1,
                RootHint: null))
            .ToList();

        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: count,
            TotalSubscribers: count,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: count,
            TopLeakGroups: groups,
            TopLeakInstances: instances);

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.EventLeakGroupCards.Should().HaveCount(count);
        section.EventLeakInstanceCards.Should().HaveCount(count);
        section.Blocks.Any(b => b is TextBlock tb && tb.Text.Contains("omitted")).Should().BeFalse();
    }

    // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): "types scanned, zero leaking"
    // must be visible in the report as a key metric, not just present in the domain model.
    [Fact]
    public void Build_ShouldSurfacePublisherTypesScannedAndCleanCount_AsKeyMetrics()
    {
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 1,
            TotalSubscribers: 1,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: 1,
            TopLeakInstances:
            [
                new EventLeakInstanceSnapshot(
                    PublisherType: "App.Publisher",
                    EventFieldName: "MyEvent",
                    IsStatic: false,
                    PublisherAddress: 0x1000,
                    SeverityScore: 10,
                    SubscriberCount: 1,
                    RootHint: null)
            ],
            PublisherTypesScanned: 50,
            CleanPublisherTypeCount: 49);

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.KeyMetrics!["publisher_types_scanned"].As<NumericMetricValue>().Value.Should().Be(50);
        section.KeyMetrics!["clean_publisher_types"].As<NumericMetricValue>().Value.Should().Be(49);
    }

    // P3-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): timer/INotifyPropertyChanged
    // categorization must reach both the key metrics and the per-group typed card.
    [Fact]
    public void Build_ShouldSurfaceTimerAndPropertyChangedCounts_AsKeyMetrics()
    {
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 2,
            TotalSubscribers: 2,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: 2,
            TopLeakGroups:
            [
                new EventLeakGroupSnapshot("System.Timers.Timer", "Elapsed", false, 10, 1, 1, 1, 1, 1,
                    IsTimerEvent: true),
                new EventLeakGroupSnapshot("App.MyViewModel", "PropertyChanged", false, 10, 1, 1, 1, 1, 1,
                    IsPropertyChangedEvent: true)
            ],
            TimerEventLeakGroupCount: 1,
            PropertyChangedEventLeakGroupCount: 1);

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        section.KeyMetrics!["timer_event_leak_groups"].As<NumericMetricValue>().Value.Should().Be(1);
        section.KeyMetrics!["property_changed_leak_groups"].As<NumericMetricValue>().Value.Should().Be(1);
        section.EventLeakGroupCards.Should().Contain(c => c.PublisherType == "System.Timers.Timer" && c.IsTimerEvent);
        section.EventLeakGroupCards.Should().Contain(c => c.PublisherType == "App.MyViewModel" && c.IsPropertyChangedEvent);
    }

    // P3-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): the histogram must render in its
    // natural (ascending-bucket) order, not sorted by count.
    [Fact]
    public void Build_ShouldRenderSubscriberCountHistogram_InBucketOrder()
    {
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 1,
            TotalSubscribers: 1,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: 1,
            SubscriberCountHistogram:
            [
                new NameCountEntry("1", 2),
                new NameCountEntry("2", 1),
                new NameCountEntry("3-5", 0),
                new NameCountEntry("101+", 9)
            ]);

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(result);

        CompactTable table = section.CompactTables!.Should().ContainSingle(t => t.Title == "Subscriber count distribution").Subject;
        table.Rows.Should().HaveCount(4);
        table.Rows[0].Values.Should().Equal("1", 2);
        table.Rows[^1].Values.Should().Equal("101+", 9);
    }
}
