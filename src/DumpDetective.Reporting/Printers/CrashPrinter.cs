using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class CrashPrinter : IAnalyzerReporter
    {
        private const int TopExceptionTypesCount = 10;

        public string AnalyzerName => "Crash Analysis";
        public string DisplayTitle => "Crash Analysis";
        public int SortOrder => 10;

        public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not CrashDomainResult domain)
                return;

            writer.WriteHeader("CRASH ANALYSIS:");
            writer.WriteSubHeading("EXCEPTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Exception Objects", $"{domain.TotalExceptions:N0}");
            writer.WriteMetric("Active Exceptions (on threads)", $"{domain.ActiveExceptions:N0}");
            writer.WriteMetric("Unique Exception Types", $"{domain.ExceptionTypeCounts.Count:N0}");

            if (domain.ActiveExceptions > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteDetailText($"⚠️  CRASH DETECTED: {domain.ActiveExceptions:N0} active exception(s) found!");
            }
            else if (domain.TotalExceptions == 0)
            {
                writer.WriteDetailBlank();
                writer.WriteDetailText("No exceptions detected in dump (likely not a crash dump).");
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("Top Exception Types:");
            int shown = 0;
            foreach (var kvp in domain.ExceptionTypeCounts.OrderByDescending(k => k.Value))
            {
                if (shown >= TopExceptionTypesCount)
                    break;

                domain.ActiveExceptionTypeCounts.TryGetValue(kvp.Key, out int activeCount);
                string activeMarker = activeCount > 0 ? $" ({activeCount:N0} active ⚠️)" : string.Empty;
                writer.WriteMetric(kvp.Key, $"{kvp.Value:N0} instance(s){activeMarker}", indentLevel: 1);
                shown++;
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LIKELY CRASH THREADS:");
            writer.WriteSeparator();
            var candidates = domain.TopCrashThreadCandidates ?? [];
            if (candidates.Count == 0)
            {
                writer.WriteDetailText("No active crash-thread candidates were detected.");
            }
            else
            {
                int rank = 1;
                foreach (var candidate in candidates)
                {
                    writer.WriteDetailText($"[{rank}] Thread {candidate.ThreadId:N0} (OS: {candidate.OSThreadId:N0})");
                    writer.WriteMetric("Active exceptions on thread", $"{candidate.ActiveExceptionCount:N0}", indentLevel: 2);
                    writer.WriteMetric("Primary exception type", candidate.PrimaryExceptionType, indentLevel: 2);

                    if (candidate.TopFrames.Count > 0)
                    {
                        writer.WriteSubHeading("Top frames:", indentLevel: 2);
                        foreach (var frame in candidate.TopFrames)
                            writer.WriteDetailText(frame, indentLevel: 3);
                    }

                    writer.WriteDetailBlank();
                    rank++;
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DETAILED EXCEPTION INFORMATION:");
            writer.WriteSeparator();
            var instances = domain.TopExceptionInstances ?? [];
            if (instances.Count == 0)
            {
                writer.WriteDetailText("No sampled exception instances available.");
            }
            else
            {
                int idx = 1;
                foreach (var ex in instances)
                {
                    writer.WriteDetailText($"[{idx}] {ex.Type}");
                    writer.WriteMetric("Address", $"0x{ex.Address:X}", indentLevel: 2);
                    if (!string.IsNullOrWhiteSpace(ex.Message))
                        writer.WriteMetric("Message", ex.Message, indentLevel: 2);
                    if (ex.HResult.HasValue)
                        writer.WriteMetric("HRESULT", $"0x{ex.HResult.Value:X8}", indentLevel: 2);
                    if (!string.IsNullOrWhiteSpace(ex.InnerExceptionType))
                        writer.WriteMetric("Inner Exception", ex.InnerExceptionType, indentLevel: 2);

                    if (ex.IsActive && ex.ThreadId.HasValue && ex.OSThreadId.HasValue)
                    {
                        writer.WriteDetailText($"⚠️  ACTIVE on Thread: {ex.ThreadId.Value:N0} (OS: {ex.OSThreadId.Value:N0})", indentLevel: 2);
                    }
                    else
                    {
                        writer.WriteMetric("Status", "Inactive (collected exception object)", indentLevel: 2);
                    }

                    if (ex.CurrentThreadFrames is { Count: > 0 })
                    {
                        writer.WriteSubHeading("Current Thread Position (exception handling):", indentLevel: 2);
                        foreach (var frame in ex.CurrentThreadFrames)
                            writer.WriteDetailText(frame, indentLevel: 3);
                    }

                    if (ex.OriginalStackTrace is { Count: > 0 })
                    {
                        writer.WriteDetailBlank();
                        writer.WriteSubHeading("🔥 ORIGINAL EXCEPTION STACK TRACE (where thrown):", indentLevel: 2);
                        foreach (var frame in ex.OriginalStackTrace)
                            writer.WriteDetailText(frame, indentLevel: 3);
                    }

                    writer.WriteDetailBlank();
                    idx++;
                }
            }

            writer.WriteDetailDivider();
        }
    }
}



