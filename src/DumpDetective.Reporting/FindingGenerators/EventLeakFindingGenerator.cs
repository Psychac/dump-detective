using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

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

        var findings = new List<InsightFinding>(capacity: 5);
        IReadOnlyList<EventLeakGroupSnapshot> groups = r.TopLeakGroups ?? [];

        int findingsToEmit = Math.Min(5, groups.Count);
        for (int i = 0; i < findingsToEmit; i++)
        {
            EventLeakGroupSnapshot group = groups[i];
            FindingSeverity severity = group.SeverityScore >= 35 ? FindingSeverity.Critical
                : group.SeverityScore >= 20 ? FindingSeverity.Warning
                : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Leak",
                Severity: severity,
                Title: $"Potential {(group.IsStatic ? "static" : "instance")} event retention in {group.PublisherType}.{group.EventFieldName}",
                Evidence: $"{group.InstanceCount:N0} publisher instance(s), {group.TotalSubscribers:N0} total subscribers, max {group.MaxSubscribers:N0} per instance.",
                Recommendation: "Ensure subscribers are unsubscribed and avoid long-lived static event publishers where possible.",
                Tags: group.IsStatic
                    ? ["event-leak", "static-event", "retention"]
                    : ["event-leak", "instance-event", "retention"],
                MetricValue: group.TotalSubscribers,
                MetricUnit: "subscribers"));
        }

        return findings;
    }
}
