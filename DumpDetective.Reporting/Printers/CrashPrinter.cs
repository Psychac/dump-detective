using System.IO;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class CrashPrinter : IAnalyzerReporter
    {
        private const int TopExceptionTypesCount = 10;

        public string AnalyzerName => "Crash Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

        public void Render(AnalyzerDomainResult result, TextWriter writer)
        {
            if (result is not CrashDomainResult domain)
                return;

            writer.WriteHeader("CRASH ANALYSIS:");
            writer.WriteLine("EXCEPTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Exception Objects: {domain.TotalExceptions:N0}");
            writer.WriteLine($"Active Exceptions (on threads): {domain.ActiveExceptions:N0}");
            writer.WriteLine($"Unique Exception Types: {domain.ExceptionTypeCounts.Count:N0}");

            if (domain.ActiveExceptions > 0)
                writer.WriteLine($"\nâš ï¸  CRASH DETECTED: {domain.ActiveExceptions:N0} active exception(s) found!");
            else if (domain.TotalExceptions == 0)
                writer.WriteLine("\nNo exceptions detected in dump (likely not a crash dump).");

            writer.WriteLine("\nTop Exception Types:");
            int shown = 0;
            foreach (var kvp in domain.ExceptionTypeCounts.OrderByDescending(k => k.Value))
            {
                if (shown >= TopExceptionTypesCount)
                    break;

                domain.ActiveExceptionTypeCounts.TryGetValue(kvp.Key, out int activeCount);
                string activeMarker = activeCount > 0 ? $" ({activeCount:N0} active âš ï¸)" : string.Empty;
                writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0} instance(s){activeMarker}");
                shown++;
            }

            writer.WriteLine("\nLIKELY CRASH THREADS:");
            writer.WriteSeparator();
            var candidates = domain.TopCrashThreadCandidates ?? [];
            if (candidates.Count == 0)
            {
                writer.WriteLine("No active crash-thread candidates were detected.");
            }
            else
            {
                int rank = 1;
                foreach (var candidate in candidates)
                {
                    writer.WriteLine($"[{rank}] Thread {candidate.ThreadId:N0} (OS: {candidate.OSThreadId:N0})");
                    writer.WriteLine($"    Active exceptions on thread: {candidate.ActiveExceptionCount:N0}");
                    writer.WriteLine($"    Primary exception type: {candidate.PrimaryExceptionType}");

                    if (candidate.TopFrames.Count > 0)
                    {
                        writer.WriteLine("    Top frames:");
                        foreach (var frame in candidate.TopFrames)
                            writer.WriteLine($"      {frame}");
                    }

                    writer.WriteLine(string.Empty);
                    rank++;
                }
            }

            writer.WriteLine("\nDETAILED EXCEPTION INFORMATION:");
            writer.WriteSeparator();
            var instances = domain.TopExceptionInstances ?? [];
            if (instances.Count == 0)
            {
                writer.WriteLine("No sampled exception instances available.");
            }
            else
            {
                int idx = 1;
                foreach (var ex in instances)
                {
                    writer.WriteLine($"[{idx}] {ex.Type}");
                    writer.WriteLine($"    Address: 0x{ex.Address:X}");
                    if (!string.IsNullOrWhiteSpace(ex.Message))
                        writer.WriteLine($"    Message: {ex.Message}");
                    if (ex.HResult.HasValue)
                        writer.WriteLine($"    HRESULT: 0x{ex.HResult.Value:X8}");
                    if (!string.IsNullOrWhiteSpace(ex.InnerExceptionType))
                        writer.WriteLine($"    Inner Exception: {ex.InnerExceptionType}");

                    if (ex.IsActive && ex.ThreadId.HasValue && ex.OSThreadId.HasValue)
                    {
                        writer.WriteLine($"    âš ï¸  ACTIVE on Thread: {ex.ThreadId.Value:N0} (OS: {ex.OSThreadId.Value:N0})");
                    }
                    else
                    {
                        writer.WriteLine("    Status: Inactive (collected exception object)");
                    }

                    if (ex.CurrentThreadFrames is { Count: > 0 })
                    {
                        writer.WriteLine("    Current Thread Position (exception handling):");
                        foreach (var frame in ex.CurrentThreadFrames)
                            writer.WriteLine($"      {frame}");
                    }

                    if (ex.OriginalStackTrace is { Count: > 0 })
                    {
                        writer.WriteLine("\n    ðŸ”¥ ORIGINAL EXCEPTION STACK TRACE (where thrown):");
                        foreach (var frame in ex.OriginalStackTrace)
                            writer.WriteLine($"      {frame}");
                    }

                    writer.WriteLine(string.Empty);
                    idx++;
                }
            }

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}



