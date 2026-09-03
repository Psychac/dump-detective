using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class EventLeakSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Event Leak Analysis";
    public string DisplayTitle => "Event & Delegate Leaks";
    public int SortOrder => 400;

    public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

    // Maps RootIndexReader.KindToString's raw ClrRootKind names to human-readable labels.
    // The domain model keeps the raw string (design §12 P2-4); translation is presentation-only.
    private static readonly System.Collections.Generic.Dictionary<string, string> RootKindLabels = new(StringComparer.Ordinal)
    {
        ["None"] = "none",
        ["FinalizerQueue"] = "finalizer queue",
        ["StrongHandle"] = "strong GC handle",
        ["PinnedHandle"] = "pinned GC handle",
        ["Stack"] = "local variable",
        ["RefCountedHandle"] = "ref-counted GC handle",
        ["AsyncPinnedHandle"] = "async pinned handle",
        ["SizedRefHandle"] = "sized ref handle",
        ["ThreadStaticVar"] = "thread-static field",
        ["StaticVar"] = "static field",
    };

    private static string TranslateRootKind(string rootKind) =>
        RootKindLabels.TryGetValue(rootKind, out string? label) ? label : rootKind;

    /// <summary>
    /// Design §4.3: <c>PublisherRootPath</c> (BFS from the publisher) is the primary
    /// "why is this alive" answer; <c>SampleSubscriberHint</c> is a cheaper fallback and is
    /// always labelled as such so a reader never mistakes it for the publisher's own path.
    /// </summary>
    private static string? FormatRootHintDisplay(EventLeakInstanceSnapshot inst)
    {
        string? publisherPath = inst.Evidence?.PublisherRootPath;
        if (!string.IsNullOrEmpty(publisherPath))
            return publisherPath;

        string? subscriberHint = inst.Evidence?.SampleSubscriberHint ?? inst.RootHint;
        return string.IsNullOrEmpty(subscriberHint) ? null : $"{TranslateRootKind(subscriberHint)} (subscriber-derived)";
    }

    private static string FormatPublisherAddress(ulong address, bool isStatic) =>
        address == 0 && isStatic ? "(static)" : $"0x{address:X}";

    private static string FormatPublisherGeneration(int generation, bool isStatic)
    {
        if (generation >= 0) return $"Gen{generation}";
        return isStatic ? "static" : "unknown";
    }

    // P3-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): a group can't be both — the two
    // pattern checks are mutually exclusive (timer types don't implement INotifyPropertyChanged
    // via their own event field named "Elapsed"/"Tick").
    private static string FormatEventCategory(bool isTimerEvent, bool isPropertyChangedEvent) =>
        isTimerEvent ? "Timer" : isPropertyChangedEvent ? "PropertyChanged" : "-";

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
        if (d.TotalEstimatedRetainedBytes > 0)
            keyMetrics["estimated_retained_bytes"] = new NumericMetricValue(d.TotalEstimatedRetainedBytes, MetricUnit.Bytes);
        // P2-2: distinguishes "checked and clean" from "not scanned at all".
        if (d.PublisherTypesScanned > 0)
        {
            keyMetrics["publisher_types_scanned"] = new NumericMetricValue(d.PublisherTypesScanned, MetricUnit.Count);
            keyMetrics["clean_publisher_types"] = new NumericMetricValue(d.CleanPublisherTypeCount, MetricUnit.Count);
        }
        // P3-3: the two most common process-lifetime event-leak categories, surfaced separately.
        if (d.TimerEventLeakGroupCount > 0)
            keyMetrics["timer_event_leak_groups"] = new NumericMetricValue(d.TimerEventLeakGroupCount, MetricUnit.Count);
        if (d.PropertyChangedEventLeakGroupCount > 0)
            keyMetrics["property_changed_leak_groups"] = new NumericMetricValue(d.PropertyChangedEventLeakGroupCount, MetricUnit.Count);

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

        // P3-4: distribution across ALL leak instances — distinguishes "one giant leaking
        // publisher" from "many small leaks adding up". Rendered in the analyzer's own natural
        // (ascending-bucket) order, not sorted by count.
        var subscriberCountHistogram = d.SubscriberCountHistogram ?? [];
        if (subscriberCountHistogram.Count > 0)
        {
            var rows = new List<CompactRow>(subscriberCountHistogram.Count);
            for (int i = 0; i < subscriberCountHistogram.Count; i++)
                rows.Add(R(subscriberCountHistogram[i].Name, subscriberCountHistogram[i].Count));
            compactTables.Add(STCompact("Subscriber count distribution",
                new[] { CH("Subscribers per instance"), CH("Instances", "number") },
                rows));
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
                new[] { CH("Publisher Type"), CH("Event Field"), CH("Subscribers","number"), CH("Instances","number"), CH("Estimated (type-average, all instances)","bytes") },
                pubRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var topSubscriberTypes = d.TopSubscriberTypesAcrossGroups ?? [];
        if (topSubscriberTypes.Count > 0)
        {
            var rows = new List<CompactRow>(topSubscriberTypes.Count);
            for (int i = 0; i < topSubscriberTypes.Count; i++)
                rows.Add(R(FormatHelper.TruncateString(topSubscriberTypes[i].Name, 100), topSubscriberTypes[i].Count));
            compactTables.Add(STCompact("Top subscriber types across all groups",
                new[] { CH("Subscriber Type"), CH("Subscriptions", "number") },
                rows));
        }

        var topHandlerMethods = d.TopHandlerMethodsAcrossGroups ?? [];
        if (topHandlerMethods.Count > 0)
        {
            var rows = new List<CompactRow>(topHandlerMethods.Count);
            for (int i = 0; i < topHandlerMethods.Count; i++)
                rows.Add(R(FormatHelper.TruncateString(topHandlerMethods[i].Name, 100), topHandlerMethods[i].Count));
            compactTables.Add(STCompact("Top handler methods across all leaking events",
                new[] { CH("Subscriber Type.Method"), CH("Subscriptions", "number") },
                rows));
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
                    Cell($"{g.DisposedButSubscribedInstances:N0}", g.DisposedButSubscribedInstances),
                    Cell(FormatEventCategory(g.IsTimerEvent, g.IsPropertyChangedEvent))]));
            }
            compactTables.Add(STCompact("Leak groups",
                new[] { CH("Publisher Type"), CH("Event Field"), CH("Static"), CH("Severity","number"), CH("Instances","number"), CH("Subscribers","number"), CH("Avg Subs"), CH("Min/Max"), CH("Estimated (type-average, all instances)","bytes"), CH("Dup Subs"), CH("Lifetime Mismatch"), CH("Disposed but Subscribed","number"), CH("Category") },
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
                    Cell(FormatPublisherAddress(inst.PublisherAddress, inst.IsStatic)),
                    Cell($"{inst.SeverityScore:N0}", inst.SeverityScore),
                    Cell($"{inst.SubscriberCount:N0}", inst.SubscriberCount),
                    Cell(FormatRootHintDisplay(inst) ?? "-"),
                    Cell(FormatPublisherGeneration(inst.PublisherGeneration, inst.IsStatic)),
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
            for (int i = 0; i < leakGroups.Count; i++)
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
                    DisposedButSubscribedInstances: group.DisposedButSubscribedInstances,
                    TopSubscriberTypes:          subTypes,
                    IsTimerEvent:                group.IsTimerEvent,
                    IsPropertyChangedEvent:      group.IsPropertyChangedEvent));
            }
        }

        // Per-instance typed cards
        var eventLeakInstanceCards = new List<EventLeakInstanceCard>();
        var instances = d.TopLeakInstances ?? [];
        if (instances.Count > 0)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                var subDetails = new List<SubscriberDetailEntry>();
                foreach (var det in inst.SubscriberDetails ?? [])
                    subDetails.Add(new SubscriberDetailEntry(det.Type, det.MethodName, det.Count, det.Size, det.SizeIsExact));

                eventLeakInstanceCards.Add(new EventLeakInstanceCard(
                    PublisherType:            inst.PublisherType,
                    EventFieldName:           inst.EventFieldName,
                    IsStatic:                 inst.IsStatic,
                    PublisherAddress:         FormatPublisherAddress(inst.PublisherAddress, inst.IsStatic),
                    SeverityScore:            inst.SeverityScore,
                    SubscriberCount:          inst.SubscriberCount,
                    RootHint:                 FormatRootHintDisplay(inst),
                    PublisherGeneration:      inst.PublisherGeneration,
                    DuplicateSubscriptionCount: inst.DuplicateSubscriptionCount,
                    IsDisposedButSubscribed:  inst.IsDisposedButSubscribed,
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
