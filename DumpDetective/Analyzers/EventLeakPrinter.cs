using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class EventLeakPrinter : IAnalyzerReporter
    {
        private const int MaxEventTypeSummaryGroups = 10;

        public string AnalyzerName => "Event Leak Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not EventLeakDomainResult domain)
                return;

            writer.WriteHeader("EVENT LEAK ANALYSIS:");
            writer.WriteLine("EVENT LEAK ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine($"Potential event leak groups: {domain.TotalEventLeakInstances:N0}");
            writer.WriteLine($"Total subscribers: {domain.TotalSubscribers:N0}");

            writer.WriteLine("\nLEAK SHAPE BREAKDOWN:");
            writer.WriteSeparator();
            writer.WriteLine($"Static event leak groups: {domain.StaticEventLeakCount:N0}");
            writer.WriteLine($"Instance event leak groups: {domain.InstanceEventLeakCount:N0}");

            writer.WriteLine("\nDETAILED INSTANCES:");
            writer.WriteSeparator();
            var topGroups = domain.TopPublisherEventsBySubscribers ?? [];
            if (topGroups.Count == 0)
            {
                writer.WriteLine("No publisher/event detail groups available.");
            }
            else
            {
                foreach (var entry in topGroups)
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 90)}: {entry.Count:N0} subscriber(s)");
            }

            writer.WriteLine("\nSUMMARY BY EVENT TYPE:");
            writer.WriteSeparator();
            var leakGroups = domain.TopLeakGroups ?? [];
            if (leakGroups.Count == 0)
            {
                writer.WriteLine("No event-group summaries available.");
            }
            else
            {
                int groupsToShow = Math.Min(MaxEventTypeSummaryGroups, leakGroups.Count);
                int idx = 1;
                for (int i = 0; i < groupsToShow; i++)
                {
                    var group = leakGroups[i];
                    string shape = group.IsStatic ? "STATIC" : "INSTANCE";
                    writer.WriteLine($"[{idx}] [{shape}] {group.PublisherType}.{group.EventFieldName} (Severity: {group.SeverityScore:N0})");
                    writer.WriteLine($"  Instance Count: {group.InstanceCount:N0}");
                    writer.WriteLine($"  Total Subscribers: {group.TotalSubscribers:N0}");
                    writer.WriteLine($"  Average Subscribers: {group.AverageSubscribers:F2}");
                    writer.WriteLine($"  Min/Max Subscribers: {group.MinSubscribers:N0}/{group.MaxSubscribers:N0}");

                    if (group.TopSubscriberTypes is { Count: > 0 })
                    {
                        writer.WriteLine("  Top Subscriber Types:");
                        foreach (var type in group.TopSubscriberTypes)
                            writer.WriteLine($"    - {type.Name} ({type.Count:N0} instance(s))");
                    }

                    writer.WriteLine(string.Empty);
                    idx++;
                }

                if (leakGroups.Count > groupsToShow)
                {
                    writer.WriteLine($"Showing top {groupsToShow:N0} event types by leak severity. {leakGroups.Count - groupsToShow:N0} additional event type(s) omitted.");
                }
            }

            writer.WriteLine("\nTOP LEAK INSTANCES:");
            writer.WriteSeparator();
            var leakInstances = domain.TopLeakInstances ?? [];
            if (leakInstances.Count == 0)
            {
                writer.WriteLine("No per-instance leak details available.");
            }
            else
            {
                int idx = 1;
                foreach (var instance in leakInstances)
                {
                    string shape = instance.IsStatic ? "STATIC" : "INSTANCE";
                    string address = instance.IsStatic ? "(static)" : $"0x{instance.PublisherAddress:X}";
                    writer.WriteLine($"[{idx}] [{shape}] {instance.PublisherType}.{instance.EventFieldName}");
                    writer.WriteLine($"  Address: {address}");
                    writer.WriteLine($"  Severity Score: {instance.SeverityScore:N0}");
                    writer.WriteLine($"  Subscribers: {instance.SubscriberCount:N0}");
                    if (!string.IsNullOrWhiteSpace(instance.RootHint))
                        writer.WriteLine($"  Root Hint: {instance.RootHint}");

                    if (instance.SubscriberTypes is { Count: > 0 })
                    {
                        writer.WriteLine("  Top Subscriber Types:");
                        foreach (var type in instance.SubscriberTypes)
                            writer.WriteLine($"    - {type}");
                    }

                    writer.WriteLine(string.Empty);
                    idx++;
                }
            }

            writer.WriteLine("\nEVENT LEAK SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.TotalEventLeakInstances > 0
                ? "⚠️  Event retention candidates detected; verify unsubscribe discipline and publisher lifetime."
                : "✅ No event retention candidates detected.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
