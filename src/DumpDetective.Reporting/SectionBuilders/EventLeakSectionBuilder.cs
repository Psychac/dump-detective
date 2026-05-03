using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class EventLeakSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int MaxGroupsToShow    = 10;
    private const int MaxInstancesToShow = 10;

    public string AnalyzerName  => "Event Leak Analysis";
    public string DisplayTitle  => "Event & Delegate Analysis";
    public int SortOrder => 80;

    public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (EventLeakDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("EVENT LEAK SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Potential Leak Groups", $"{d.TotalEventLeakInstances:N0}", d.TotalEventLeakInstances));
        blocks.Add(M("Total Subscribers",     $"{d.TotalSubscribers:N0}",        d.TotalSubscribers));
        blocks.Add(M("Static Event Leaks",    $"{d.StaticEventLeakCount:N0}",    d.StaticEventLeakCount));
        blocks.Add(M("Instance Event Leaks",  $"{d.InstanceEventLeakCount:N0}",  d.InstanceEventLeakCount));
        if (d.TotalEventsScanned > 0)
            blocks.Add(M("Events Scanned",        $"{d.TotalEventsScanned:N0}",       d.TotalEventsScanned));
        if (d.TotalPublisherInstances > 0)
            blocks.Add(M("Publisher Instances",   $"{d.TotalPublisherInstances:N0}",  d.TotalPublisherInstances));

        var topPublishers = d.TopPublisherEvents ?? [];
        if (topPublishers.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP PUBLISHER EVENTS BY SUBSCRIBER COUNT"));
            blocks.Add(Divider());

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
            blocks.Add(new TableBlock("Publisher events by subscriber count",
                ["Publisher Type", "Event Field", "Subscribers", "Instances", "Est. Retained"],
                pubRows));
        }

        var leakGroups = d.TopLeakGroups ?? [];
        if (leakGroups.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("LEAK GROUP DETAILS"));
            blocks.Add(Divider());

            int groupLimit = Math.Min(leakGroups.Count, MaxGroupsToShow);
            for (int i = 0; i < groupLimit; i++)
            {
                var group = leakGroups[i];
                string shape = group.IsStatic ? "STATIC" : "INSTANCE";
                blocks.Add(CollapseBegin($"[{i + 1}] [{shape}] {group.PublisherType}.{group.EventFieldName}  (Severity: {group.SeverityScore})"));
                blocks.Add(M("Instance Count",       $"{group.InstanceCount:N0}",            group.InstanceCount,   indent: 1));
                blocks.Add(M("Total Subscribers",    $"{group.TotalSubscribers:N0}",          group.TotalSubscribers, indent: 1));
                blocks.Add(M("Avg Subscribers",      $"{group.AverageSubscribers:F2}",        indent: 1));
                blocks.Add(M("Min/Max Subscribers",  $"{group.MinSubscribers}/{group.MaxSubscribers}", indent: 1));
                if (group.EstimatedSubscriberRetainedBytes > 0)
                    blocks.Add(M("Est. Retained Bytes",  FormatHelper.FormatBytes(group.EstimatedSubscriberRetainedBytes), (double)group.EstimatedSubscriberRetainedBytes, indent: 1));
                if (group.HasDuplicateSubscriptions)
                    blocks.Add(M("Duplicate Subscriptions", "YES \u2013 same subscriber registered multiple times", indent: 1));
                if (group.HasLifetimeMismatch)
                    blocks.Add(M("Lifetime Mismatch", "YES \u2013 Gen2 publisher retaining Gen0/Gen1 subscribers", indent: 1));
                if (group.OrphanedSubscriberInstances > 0)
                    blocks.Add(M("Orphaned Sub. Instances", $"{group.OrphanedSubscriberInstances:N0} instance(s) with dead-subscriber pattern", (double)group.OrphanedSubscriberInstances, indent: 1));

                var subTypes = group.TopSubscriberTypes ?? [];
                if (subTypes.Count > 0)
                {
                    var subRows = new List<TableRow>(subTypes.Count);
                    for (int j = 0; j < subTypes.Count; j++)
                        subRows.Add(new TableRow([Cell(subTypes[j].Name), Cell($"{subTypes[j].Count:N0}", subTypes[j].Count)]));
                    blocks.Add(new TableBlock(null, ["Subscriber Type", "Count"], subRows));
                }
                blocks.Add(CollapseEnd());
            }

            if (leakGroups.Count > groupLimit)
                blocks.Add(T($"Showing top {groupLimit} event types. {leakGroups.Count - groupLimit} additional group(s) omitted."));
        }

        var instances = d.TopLeakInstances ?? [];
        if (instances.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP LEAK INSTANCES"));
            blocks.Add(Divider());

            int instLimit = Math.Min(instances.Count, MaxInstancesToShow);
            for (int i = 0; i < instLimit; i++)
            {
                var inst = instances[i];
                string shape = inst.IsStatic ? "STATIC" : "INSTANCE";
                blocks.Add(CollapseBegin($"[{i + 1}] [{shape}] {inst.PublisherType}.{inst.EventFieldName}  ({inst.SubscriberCount} subscribers)"));
                blocks.Add(M("Publisher Address", $"0x{inst.PublisherAddress:X}", indent: 1));
                blocks.Add(M("Severity Score",    $"{inst.SeverityScore:N0}",     inst.SeverityScore, indent: 1));
                if (!string.IsNullOrWhiteSpace(inst.RootHint))
                    blocks.Add(M("Root Hint", inst.RootHint, indent: 1));
                if (inst.PublisherGeneration >= 0)
                    blocks.Add(M("Publisher Generation", $"Gen{inst.PublisherGeneration}", inst.PublisherGeneration, indent: 1));
                if (inst.DuplicateSubscriptionCount > 0)
                    blocks.Add(M("Duplicate Subscriptions", $"{inst.DuplicateSubscriptionCount:N0} extra registration(s)", inst.DuplicateSubscriptionCount, indent: 1));
                if (inst.OrphanedSubscriberCount > 0)
                    blocks.Add(M("Orphaned Subscribers", $"{inst.OrphanedSubscriberCount:N0} not independently GC-rooted", inst.OrphanedSubscriberCount, indent: 1));
                if (inst.HasLifetimeMismatch)
                    blocks.Add(M("Lifetime Mismatch", "YES \u2013 Gen2 publisher retaining Gen0/Gen1 subscribers", indent: 1));

                var subDetails = inst.SubscriberDetails ?? [];
                if (subDetails.Count > 0)
                {
                    var detailRows = new List<TableRow>(subDetails.Count);
                    for (int j = 0; j < subDetails.Count; j++)
                    {
                        var det = subDetails[j];
                        detailRows.Add(new TableRow([
                            Cell(FormatHelper.TruncateString(det.Type, 60)),
                            Cell(det.MethodName != null ? FormatHelper.TruncateString(det.MethodName, 70) : "-"),
                            Cell($"{det.Count:N0}", det.Count),
                            Cell(det.Size > 0 ? FormatHelper.FormatBytes(det.Size) : "-")]));
                    }
                    blocks.Add(new TableBlock("Subscribers", ["Type", "Handler Method", "Count", "Avg Size"], detailRows));
                }
                blocks.Add(CollapseEnd());
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}
