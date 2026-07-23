using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class EventLeakFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Event Leak Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is EventLeakDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not EventLeakDomainResult r) return [];

        if (r.TotalEventLeakInstances == 0)
        {
            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Leak",
                    Severity: FindingSeverity.Info,
                    Title: "No event-leak signatures detected",
                    Evidence: "No significant event subscription patterns were detected.",
                    Recommendation: "No immediate action required for event retention patterns.",
                    Tags: ["event", "leak", "subscriptions"],
                    MetricValue: 0,
                    MetricUnit: "subscribers")
            ];
        }

        IReadOnlyList<EventLeakGroupSnapshot> groups = r.TopLeakGroups ?? [];
        IReadOnlyList<EventLeakInstanceSnapshot> instances = r.TopLeakInstances ?? [];
        var findings = new List<InsightFinding>(capacity: 2);

        AddAggregateFinding(findings, groups, instances, isStatic: false, r.InstanceEventLeakCount);
        AddAggregateFinding(findings, groups, instances, isStatic: true, r.StaticEventLeakCount);

        return findings;
    }

    private static void AddAggregateFinding(
        List<InsightFinding> findings,
        IReadOnlyList<EventLeakGroupSnapshot> groups,
        IReadOnlyList<EventLeakInstanceSnapshot> instances,
        bool isStatic,
        int fallbackGroupCount)
    {
        int groupCount = 0;
        int totalPublisherInstances = 0;
        int totalSubscribers = 0;
        int maxSubscribersPerInstance = 0;
        int maxSeverityScore = 0;

        string topPublisherType = string.Empty;
        string topEventFieldName = string.Empty;
        int topGroupSubscribers = -1;

        string exampleA = string.Empty;
        string exampleB = string.Empty;

        for (int i = 0; i < groups.Count; i++)
        {
            EventLeakGroupSnapshot group = groups[i];
            if (group.IsStatic != isStatic) continue;

            groupCount++;
            totalPublisherInstances += group.InstanceCount;
            totalSubscribers += group.TotalSubscribers;

            if (group.MaxSubscribers > maxSubscribersPerInstance)
                maxSubscribersPerInstance = group.MaxSubscribers;

            if (group.SeverityScore > maxSeverityScore)
                maxSeverityScore = group.SeverityScore;

            if (group.TotalSubscribers > topGroupSubscribers)
            {
                topGroupSubscribers = group.TotalSubscribers;
                topPublisherType = group.PublisherType;
                topEventFieldName = group.EventFieldName;
            }

            string example = group.PublisherType + "." + group.EventFieldName;
            if (string.IsNullOrEmpty(exampleA)) exampleA = example;
            else if (string.IsNullOrEmpty(exampleB) && !string.Equals(exampleA, example, StringComparison.Ordinal)) exampleB = example;
        }

        if (groupCount == 0)
        {
            groupCount = fallbackGroupCount;
        }

        if (groupCount <= 0) return;

        FindingSeverity severity = maxSeverityScore >= 35 ? FindingSeverity.Critical
            : maxSeverityScore >= 20 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        string leakKind = isStatic ? "static" : "instance";
        string title = groupCount == 1
            ? $"Potential {leakKind} event retention pattern detected"
            : $"Potential {leakKind} event retention patterns detected ({groupCount:N0})";

        string evidence = $"{groupCount:N0} leak group(s), {totalPublisherInstances:N0} publisher instance(s), {totalSubscribers:N0} total subscribers";
        if (maxSubscribersPerInstance > 0)
            evidence += $", max {maxSubscribersPerInstance:N0} per instance";

        if (!string.IsNullOrWhiteSpace(topPublisherType) && !string.IsNullOrWhiteSpace(topEventFieldName))
            evidence += $". Top group: {topPublisherType}.{topEventFieldName} ({topGroupSubscribers:N0} subscribers).";
        else
            evidence += ".";

        if (!string.IsNullOrEmpty(exampleA))
        {
            evidence += " Examples: " + exampleA;
            if (!string.IsNullOrEmpty(exampleB)) evidence += ", " + exampleB;
            evidence += ".";
        }

        Evidence? topEvidence = null;
        int topSubscriberCount = -1;
        for (int i = 0; i < instances.Count; i++)
        {
            EventLeakInstanceSnapshot instance = instances[i];
            if (instance.IsStatic != isStatic) continue;
            if (instance.SubscriberCount > topSubscriberCount)
            {
                topSubscriberCount = instance.SubscriberCount;
                topEvidence = instance.Evidence;
            }
        }

        findings.Add(new InsightFinding(
            Analyzer: "Event Leak Analysis",
            Category: "Leak",
            Severity: severity,
            Title: title,
            Evidence: evidence,
            Recommendation: "Ensure subscribers are unsubscribed and avoid long-lived static event publishers where possible.",
            Tags: isStatic
                ? ["event-leak", "static-event", "retention"]
                : ["event-leak", "instance-event", "retention"],
            MetricValue: totalSubscribers,
            MetricUnit: "subscribers",
            ConfidenceScore: EvidenceConfidence.Compute(topEvidence)));
    }
}
