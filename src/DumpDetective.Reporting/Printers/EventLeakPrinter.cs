using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class EventLeakPrinter : IAnalyzerReporter
    {
        private const int MaxEventTypeSummaryGroups = 10;
        private const int MaxTopLeakInstances = 10;

        public string AnalyzerName => "Event Leak Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not EventLeakDomainResult domain)
                return;

            writer.WriteHeader("EVENT LEAK ANALYSIS:");
            writer.WriteSubHeading("EVENT LEAK ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteMetric("Potential event leak groups", $"{domain.TotalEventLeakInstances:N0}");
            writer.WriteMetric("Total subscribers", $"{domain.TotalSubscribers:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LEAK SHAPE BREAKDOWN:");
            writer.WriteSeparator();
            writer.WriteMetric("Static event leak groups", $"{domain.StaticEventLeakCount:N0}");
            writer.WriteMetric("Instance event leak groups", $"{domain.InstanceEventLeakCount:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DETAILED INSTANCES:");
            writer.WriteSeparator();
            var topGroups = domain.TopPublisherEventsBySubscribers ?? [];
            if (topGroups.Count == 0)
            {
                writer.WriteDetailText("No publisher/event detail groups available.");
            }
            else
            {
                foreach (var entry in topGroups)
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 90), $"{entry.Count:N0} subscriber(s)", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("SUMMARY BY EVENT TYPE:");
            writer.WriteSeparator();
            var leakGroups = domain.TopLeakGroups ?? [];
            if (leakGroups.Count == 0)
            {
                writer.WriteDetailText("No event-group summaries available.");
            }
            else
            {
                int groupsToShow = Math.Min(MaxEventTypeSummaryGroups, leakGroups.Count);
                int idx = 1;
                for (int i = 0; i < groupsToShow; i++)
                {
                    var group = leakGroups[i];
                    string shape = group.IsStatic ? "STATIC" : "INSTANCE";
                    writer.WriteDetailText($"[{idx}] [{shape}] {group.PublisherType}.{group.EventFieldName} (Severity: {group.SeverityScore:N0})");
                    writer.WriteMetric("Instance Count", $"{group.InstanceCount:N0}", indentLevel: 1);
                    writer.WriteMetric("Total Subscribers", $"{group.TotalSubscribers:N0}", indentLevel: 1);
                    writer.WriteMetric("Average Subscribers", $"{group.AverageSubscribers:F2}", indentLevel: 1);
                    writer.WriteMetric("Min/Max Subscribers", $"{group.MinSubscribers:N0}/{group.MaxSubscribers:N0}", indentLevel: 1);

                    if (group.TopSubscriberTypes is { Count: > 0 })
                    {
                        writer.WriteSubHeading("Top Subscriber Types:", indentLevel: 1);
                        foreach (var type in group.TopSubscriberTypes)
                            writer.WriteDetailBullet($"{type.Name} ({type.Count:N0} instance(s))", indentLevel: 2);
                    }

                    writer.WriteDetailBlank();
                    idx++;
                }

                if (leakGroups.Count > groupsToShow)
                {
                    writer.WriteDetailText($"Showing top {groupsToShow:N0} event types by leak severity. {leakGroups.Count - groupsToShow:N0} additional event type(s) omitted.");
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP LEAK INSTANCES:");
            writer.WriteSeparator();
            var leakInstances = domain.TopLeakInstances ?? [];
            if (leakInstances.Count == 0)
            {
                writer.WriteDetailText("No per-instance leak details available.");
            }
            else
            {
                int instancesToShow = Math.Min(MaxTopLeakInstances, leakInstances.Count);
                int idx = 1;
                for (int i = 0; i < instancesToShow; i++)
                {
                    var instance = leakInstances[i];
                    string shape = instance.IsStatic ? "STATIC" : "INSTANCE";
                    string address = instance.IsStatic ? "(static)" : $"0x{instance.PublisherAddress:X}";
                    writer.WriteDetailText($"[{idx}] [{shape}] {instance.PublisherType}.{instance.EventFieldName}");
                    writer.WriteMetric("Address", address, indentLevel: 1);
                    writer.WriteMetric("Severity Score", $"{instance.SeverityScore:N0}", indentLevel: 1);
                    writer.WriteMetric("Subscribers", $"{instance.SubscriberCount:N0}", indentLevel: 1);
                    if (!string.IsNullOrWhiteSpace(instance.RootHint))
                        writer.WriteMetric("Root Hint", instance.RootHint, indentLevel: 1);

                    if (instance.SubscriberTypes is { Count: > 0 })
                    {
                        writer.WriteSubHeading("Top Subscriber Types:", indentLevel: 1);
                        foreach (var type in instance.SubscriberTypes)
                            writer.WriteDetailBullet(type, indentLevel: 2);
                    }

                    writer.WriteDetailBlank();
                    idx++;
                }

                if (leakInstances.Count > instancesToShow)
                {
                    writer.WriteDetailText($"Showing top {instancesToShow:N0} leak instance(s) by severity. {leakInstances.Count - instancesToShow:N0} additional leak instance(s) omitted.");
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("EVENT LEAK SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.TotalEventLeakInstances > 0
                ? "⚠️  Event retention candidates detected; verify unsubscribe discipline and publisher lifetime."
                : "✅ No event retention candidates detected.");
            writer.WriteDetailDivider();
        }
    }
}



