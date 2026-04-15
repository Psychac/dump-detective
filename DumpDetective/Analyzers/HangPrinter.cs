using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class HangPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Hang Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is HangDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not HangDomainResult domain)
                return;

            writer.WriteHeader("HANG ANALYSIS:");
            writer.WriteLine("HANG INDICATORS:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Alive Threads: {domain.TotalAliveThreads:N0}");
            writer.WriteLine($"Waiting/Blocked Threads: {domain.WaitingThreadCount:N0}");
            writer.WriteLine($"Waiting Thread Percentage: {domain.WaitingPercent:F1}%");
            writer.WriteLine($"Thread Health Score: {domain.HealthScore}/100");

            if (domain.WaitingPercent >= 80)
                writer.WriteLine("\n⚠️  SEVERE HANG risk detected.");
            else if (domain.WaitingPercent >= 50)
                writer.WriteLine("\n⚠️  POSSIBLE HANG risk detected.");

            writer.WriteLine("\nWAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitCategoryBreakdown.Count == 0)
            {
                writer.WriteLine("No waiting categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitCategoryBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
            }

            writer.WriteLine("\nTHREAD POOL STATUS:");
            writer.WriteSeparator();
            writer.WriteLine($"Queued Work Items: {domain.QueuedWorkItems:N0}");
            writer.WriteLine($"Pending Tasks: {domain.PendingTasks:N0}");
            writer.WriteLine($"Faulted Tasks: {domain.FaultedTasks:N0}");
            writer.WriteLine($"Canceled Tasks: {domain.CanceledTasks:N0}");

            writer.WriteLine("\nASYNC TASK ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine("Detailed continuation-type breakdown is omitted in this mode.");

            writer.WriteLine("\nDEADLOCK SUSPICION:");
            writer.WriteSeparator();
            writer.WriteLine(domain.WaitingPercent >= 80
                ? "⚠️  Severe wait saturation suggests potential deadlock/contention storm."
                : "ℹ️  No severe deadlock suspicion signal detected from headline metrics.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
