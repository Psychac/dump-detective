using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class LockGraphPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Lock Graph Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is LockGraphDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not LockGraphDomainResult domain)
                return;

            writer.WriteHeader("LOCK GRAPH ANALYSIS:");
            writer.WriteLine("LOCK CONTENTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Held locks: {domain.TotalHeldLocks:N0}");
            writer.WriteLine($"Contested locks: {domain.ContestedLockCount:N0}");
            writer.WriteLine($"Max waiters on single lock: {domain.MaxWaitersOnSingleLock:N0}");

            writer.WriteLine("\nLOCK CONTENTION HOTSPOTS:");
            writer.WriteSeparator();
            var topTypes = domain.TopContestedLockTypes ?? [];
            if (topTypes.Count == 0)
            {
                writer.WriteLine("No contested lock hotspot details available.");
            }
            else
            {
                foreach (var entry in topTypes.Take(8))
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 70)}: {entry.Count:N0} cumulative waiter(s)");
            }

            writer.WriteLine("\nDEADLOCK CANDIDATES:");
            writer.WriteSeparator();
            writer.WriteLine($"Deadlock candidates: {domain.DeadlockCandidateCount:N0}");
            writer.WriteLine(domain.DeadlockCandidateCount >= 2
                ? "⚠️  Probable deadlock pattern detected."
                : domain.ContestedLockCount > 0
                    ? "⚠️  Lock contention present; monitor lock acquisition order."
                    : "✅ No lock contention/deadlock candidates detected.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
