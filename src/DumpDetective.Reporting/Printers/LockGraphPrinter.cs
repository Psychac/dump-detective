using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class LockGraphPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Lock Graph Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is LockGraphDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not LockGraphDomainResult domain)
                return;

            writer.WriteHeader("LOCK GRAPH ANALYSIS:");
            writer.WriteSubHeading("LOCK CONTENTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Held locks", $"{domain.TotalHeldLocks:N0}");
            writer.WriteMetric("Contested locks", $"{domain.ContestedLockCount:N0}");
            writer.WriteMetric("Max waiters on single lock", $"{domain.MaxWaitersOnSingleLock:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LOCK CONTENTION HOTSPOTS:");
            writer.WriteSeparator();
            var topTypes = domain.TopContestedLockTypes ?? [];
            if (topTypes.Count == 0)
            {
                writer.WriteDetailText("No contested lock hotspot details available.");
            }
            else
            {
                foreach (var entry in topTypes.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 70), $"{entry.Count:N0} cumulative waiter(s)", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DEADLOCK CANDIDATES:");
            writer.WriteSeparator();
            writer.WriteMetric("Deadlock candidates", $"{domain.DeadlockCandidateCount:N0}");
            writer.WriteDetailText(domain.DeadlockCandidateCount >= 2
                ? "⚠️  Probable deadlock pattern detected."
                : domain.ContestedLockCount > 0
                    ? "⚠️  Lock contention present; monitor lock acquisition order."
                    : "✅ No lock contention/deadlock candidates detected.");
            writer.WriteDetailDivider();
        }
    }
}



