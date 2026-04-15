using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class MemoryLeakPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Memory Leak Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryLeakDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not MemoryLeakDomainResult domain)
                return;

            writer.WriteHeader("MEMORY LEAK ANALYSIS:");
            writer.WriteLine("FINALIZER SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Finalizer queue objects: {domain.FinalizerQueueCount:N0}");

            writer.WriteLine("\nDUPLICATE STRING SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Duplicate string patterns: {domain.DuplicateStringPatternCount:N0}");
            writer.WriteLine($"Estimated duplicate-string waste: {FormatHelper.FormatBytes(domain.DuplicateStringWastedBytes)}");

            writer.WriteLine("\nHIGH-REFERENCE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Highly referenced objects: {domain.HighlyReferencedObjectCount:N0}");

            if (domain.SkippedReferenceAddresses > 0)
                writer.WriteLine($"Reference tracking cap hit; skipped addresses: {domain.SkippedReferenceAddresses:N0}");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
