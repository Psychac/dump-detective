using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class ThreadPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Thread Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not ThreadDomainResult domain)
                return;

            writer.WriteHeader("THREAD ANALYSIS:");
            writer.WriteLine("THREAD TRIAGE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Alive threads: {domain.AliveThreadCount:N0}");
            writer.WriteLine($"Blocked-pattern threads: {domain.BlockedThreadCount:N0}");
            writer.WriteLine($"Lock-holding threads: {domain.LockHoldingThreadCount:N0}");
            writer.WriteLine($"Threads with active exceptions: {domain.ThreadsWithActiveExceptionsCount:N0}");

            writer.WriteLine("\nWAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitPatternBreakdown.Count == 0)
            {
                writer.WriteLine("No wait categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitPatternBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
            }

            writer.WriteLine("\nTHREAD HEALTH SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.BlockedThreadCount >= 10 || domain.ThreadsWithActiveExceptionsCount > 0
                ? "⚠️  Thread-state triage indicates elevated hang/contention risk."
                : "✅ Thread-state profile appears stable for this snapshot.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
