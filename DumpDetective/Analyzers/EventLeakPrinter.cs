using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class EventLeakPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Event Leak Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is EventLeakDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not EventLeakDomainResult domain)
                return;

            writer.WriteHeader("EVENT LEAK ANALYSIS:");
            writer.WriteLine("EVENT RETENTION SUMMARY:");
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
                foreach (var entry in topGroups.Take(8))
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 90)}: {entry.Count:N0} subscriber(s)");
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
