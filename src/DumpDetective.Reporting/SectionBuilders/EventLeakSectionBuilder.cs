using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class EventLeakSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int MaxGroupsToShow = 10;
    private const int MaxInstancesToShow = 10;

    public string AnalyzerName => "Event Leak Analysis";
    public string DisplayTitle => "Event & Delegate Leaks";
    public int SortOrder => 400;

    public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (EventLeakDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["potential_leak_groups"] = new NumericMetricValue(d.TotalEventLeakInstances, MetricUnit.Count),
            ["total_subscribers"] = new NumericMetricValue(d.TotalSubscribers, MetricUnit.Count),
            ["static_event_leaks"] = new NumericMetricValue(d.StaticEventLeakCount, MetricUnit.Count),
            ["instance_event_leaks"] = new NumericMetricValue(d.InstanceEventLeakCount, MetricUnit.Count),
        };
        if (d.TotalEventsScanned > 0)
            keyMetrics["events_scanned"] = new NumericMetricValue(d.TotalEventsScanned, MetricUnit.Count);
        if (d.TotalPublisherInstances > 0)
            keyMetrics["publisher_instances"] = new NumericMetricValue(d.TotalPublisherInstances, MetricUnit.Count);

        var instancesWithGeneration = d.TopLeakInstances ?? [];
        if (instancesWithGeneration.Count > 0)
        {
            int gen0 = 0; int gen1 = 0; int gen2 = 0; int unknown = 0;
            for (int i = 0; i < instancesWithGeneration.Count; i++)
            {
                int generation = instancesWithGeneration[i].PublisherGeneration;
                if (generation == 0) gen0++;
                else if (generation == 1) gen1++;
                else if (generation >= 2) gen2++;
                else unknown++;
            }
            compactTables.Add(STCompact("Publisher generation distribution", new[] { CH("Generation"), CH("Count","number") },
                new[] { R("Gen0", gen0), R("Gen1", gen1), R("Gen2", gen2), R("Unknown", unknown) }));
        }

        var topPublishers = d.TopPublisherEvents ?? [];
        if (topPublishers.Count > 0)
        {
            var pubRows = new List<TableRow>(topPublishers.Count);
            for (int i = 0; i < topPublishers.Count; i++)
            {
                var p = topPublishers[i];
                pubRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(p.PublisherType, 70)),
                    Cell(FormatHelper.TruncateString(p.EventFieldName, 40)),
                    Cell($"{p.TotalSubscribers:N0}", p.TotalSubscribers),
                    Cell($"{p.InstanceCount:N0}", p.InstanceCount),
                    Cell(p.EstimatedRetainedBytes > 0 ? FormatHelper.FormatBytes(p.EstimatedRetainedBytes) : "-")]));
            }
            compactTables.Add(STCompact("Publisher events by subscriber count",
                new[] { CH("Publisher Type"), CH("Event Field"), CH("Subscribers","number"), CH("Instances","number"), CH("Est. Retained","bytes") },
                pubRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var leakGroupsForTable = d.TopLeakGroups ?? [];
        if (leakGroupsForTable.Count > 0)
        {
            var groupRows = new List<TableRow>(leakGroupsForTable.Count);
            for (int i = 0; i < leakGroupsForTable.Count; i++)
            {
                var g = leakGroupsForTable[i];
                groupRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(g.PublisherType, 70)),
                    Cell(FormatHelper.TruncateString(g.EventFieldName, 40)),
                    Cell(g.IsStatic ? "Yes" : "No"),
                    Cell($"{g.SeverityScore:N0}", g.SeverityScore),
                    Cell($"{g.InstanceCount:N0}", g.InstanceCount),
                    Cell($"{g.TotalSubscribers:N0}", g.TotalSubscribers),
                    Cell($"{g.AverageSubscribers:F1}"),
                    Cell($"{g.MinSubscribers}/{g.MaxSubscribers}"),
                    Cell(g.EstimatedSubscriberRetainedBytes > 0 ? FormatHelper.FormatBytes(g.EstimatedSubscriberRetainedBytes) : "-"),
                    Cell(g.HasDuplicateSubscriptions ? "Yes" : "No"),
                    Cell(g.HasLifetimeMismatch ? "Yes" : "No"),
                    Cell($"{g.OrphanedSubscriberInstances:N0}", g.OrphanedSubscriberInstances)]));
            }
            compactTables.Add(STCompact("Leak groups",
                new[] { CH("Publisher Type"), CH("Event Field"), CH("Static"), CH("Severity","number"), CH("Instances","number"), CH("Subscribers","number"), CH("Avg Subs"), CH("Min/Max"), CH("Est. Retained","bytes"), CH("Dup Subs"), CH("Lifetime Mismatch"), CH("Orphaned","number") },
                groupRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var leakInstancesForTable = d.TopLeakInstances ?? [];
        if (leakInstancesForTable.Count > 0)
        {
            var instRows = new List<TableRow>(leakInstancesForTable.Count);
            for (int i = 0; i < leakInstancesForTable.Count; i++)
            {
                var inst = leakInstancesForTable[i];
                instRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(inst.PublisherType, 70)),
                    Cell(FormatHelper.TruncateString(inst.EventFieldName, 40)),
                    Cell(inst.IsStatic ? "Yes" : "No"),
                    Cell($"0x{inst.PublisherAddress:X}"),
                    Cell($"{inst.SeverityScore:N0}", inst.SeverityScore),
                    Cell($"{inst.SubscriberCount:N0}", inst.SubscriberCount),
                    Cell(inst.RootHint ?? "-"),
                    Cell(inst.PublisherGeneration >= 0 ? $"Gen{inst.PublisherGeneration}" : "-"),
                    Cell($"{inst.DuplicateSubscriptionCount:N0}", inst.DuplicateSubscriptionCount)]));
            }
            compactTables.Add(STCompact("Top leak instances",
                new[] { CH("Publisher Type"), CH("Event Field"), CH("Static"), CH("Publisher Addr"), CH("Severity","number"), CH("Subscribers","number"), CH("Root Hint"), CH("Publisher Gen"), CH("Dup Subs","number") },
                instRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Per-group typed cards
        var eventLeakGroupCards = new List<EventLeakGroupCard>();
        var leakGroups = d.TopLeakGroups ?? [];
        if (leakGroups.Count > 0)
        {
            var allInstances = d.TopLeakInstances ?? [];
            int groupLimit = Math.Min(leakGroups.Count, MaxGroupsToShow);
            for (int i = 0; i < groupLimit; i++)
            {
                var group = leakGroups[i];

                int matchingInstances = 0; int gen2Instances = 0;
                for (int j = 0; j < allInstances.Count; j++)
                {
                    EventLeakInstanceSnapshot inst = allInstances[j];
                    if (inst.PublisherType != group.PublisherType || inst.EventFieldName != group.EventFieldName || inst.IsStatic != group.IsStatic)
                        continue;
                    matchingInstances++;
                    if (inst.PublisherGeneration >= 2) gen2Instances++;
                }
                double gen2Pct = matchingInstances > 0 ? gen2Instances * 100.0 / matchingInstances : 0;

                var subTypes = new List<SubscriberDetailEntry>();
                foreach (var e in group.TopSubscriberTypes ?? [])
                    subTypes.Add(new SubscriberDetailEntry(e.Name, null, e.Count, 0));

                eventLeakGroupCards.Add(new EventLeakGroupCard(
                    PublisherType:               group.PublisherType,
                    EventFieldName:              group.EventFieldName,
                    IsStatic:                    group.IsStatic,
                    SeverityScore:               group.SeverityScore,
                    InstanceCount:               group.InstanceCount,
                    TotalSubscribers:            group.TotalSubscribers,
                    AverageSubscribers:          group.AverageSubscribers,
                    MinSubscribers:              group.MinSubscribers,
                    MaxSubscribers:              group.MaxSubscribers,
                    Gen2PublisherPercent:         gen2Pct,
                    EstimatedRetainedBytes:      group.EstimatedSubscriberRetainedBytes,
                    HasDuplicateSubscriptions:   group.HasDuplicateSubscriptions,
                    HasLifetimeMismatch:         group.HasLifetimeMismatch,
                    OrphanedSubscriberInstances: group.OrphanedSubscriberInstances,
                    TopSubscriberTypes:          subTypes));
            }
            if (leakGroups.Count > groupLimit)
                blocks.Add(T($"Showing top {groupLimit} event types. {leakGroups.Count - groupLimit} additional group(s) omitted."));
        }

        // Per-instance typed cards
        var eventLeakInstanceCards = new List<EventLeakInstanceCard>();
        var instances = d.TopLeakInstances ?? [];
        if (instances.Count > 0)
        {
            int instLimit = Math.Min(instances.Count, MaxInstancesToShow);
            for (int i = 0; i < instLimit; i++)
            {
                var inst = instances[i];
                var subDetails = new List<SubscriberDetailEntry>();
                foreach (var det in inst.SubscriberDetails ?? [])
                    subDetails.Add(new SubscriberDetailEntry(det.Type, det.MethodName, det.Count, det.Size));

                eventLeakInstanceCards.Add(new EventLeakInstanceCard(
                    PublisherType:            inst.PublisherType,
                    EventFieldName:           inst.EventFieldName,
                    IsStatic:                 inst.IsStatic,
                    PublisherAddress:         $"0x{inst.PublisherAddress:X}",
                    SeverityScore:            inst.SeverityScore,
                    SubscriberCount:          inst.SubscriberCount,
                    RootHint:                 inst.RootHint,
                    PublisherGeneration:      inst.PublisherGeneration,
                    DuplicateSubscriptionCount: inst.DuplicateSubscriptionCount,
                    OrphanedSubscriberCount:  inst.OrphanedSubscriberCount,
                    HasLifetimeMismatch:      inst.HasLifetimeMismatch,
                    SubscriberDetails:        subDetails.Count > 0 ? subDetails : null));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            EventLeakGroupCards:    eventLeakGroupCards.Count > 0 ? eventLeakGroupCards : null,
            EventLeakInstanceCards: eventLeakInstanceCards.Count > 0 ? eventLeakInstanceCards : null);
    }
}
