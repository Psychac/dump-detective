using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class CrashPrinter : IAnalyzerReporter
    {
        private const int TopExceptionTypesCount = 10;

        public string AnalyzerName => "Crash Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
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
                writer.WriteLine($"\n⚠️  CRASH DETECTED: {domain.ActiveExceptions:N0} active exception(s) found!");
            else if (domain.TotalExceptions == 0)
                writer.WriteLine("\nNo exceptions detected in dump (likely not a crash dump).");

            writer.WriteLine("\nTop Exception Types:");
            int shown = 0;
            foreach (var kvp in domain.ExceptionTypeCounts.OrderByDescending(k => k.Value))
            {
                if (shown >= TopExceptionTypesCount)
                    break;

                domain.ActiveExceptionTypeCounts.TryGetValue(kvp.Key, out int activeCount);
                string activeMarker = activeCount > 0 ? $" ({activeCount:N0} active ⚠️)" : string.Empty;
                writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0} instance(s){activeMarker}");
                shown++;
            }

            writer.WriteLine("\nLIKELY CRASH THREADS:");
            writer.WriteSeparator();
            writer.WriteLine("Detailed thread candidate ranking is omitted in this mode.");

            writer.WriteLine("\nDETAILED EXCEPTION INFORMATION:");
            writer.WriteSeparator();
            writer.WriteLine("Detailed per-instance exception stack information is omitted in this mode.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
