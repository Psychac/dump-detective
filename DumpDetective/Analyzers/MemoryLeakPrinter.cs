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
            var finalizerTypes = domain.TopFinalizerTypes ?? [];
            if (finalizerTypes.Count > 0)
            {
                writer.WriteLine("\nTop types in finalizer queue:");
                foreach (var type in finalizerTypes)
                    writer.WriteLine($"  {type.Name}: {type.Count:N0} object(s)");
            }

            writer.WriteLine("\nDUPLICATE STRING SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Duplicate string patterns: {domain.DuplicateStringPatternCount:N0}");
            writer.WriteLine($"Estimated duplicate-string waste: {FormatHelper.FormatBytes(domain.DuplicateStringWastedBytes)}");
            var duplicateStrings = domain.TopDuplicateStrings ?? [];
            if (duplicateStrings.Count > 0)
            {
                writer.WriteLine("\nMost duplicated strings (potential string pooling opportunities):");
                writer.WriteLine($"{"String Preview",-50} {"Count",10} {"Wasted",12}");
                foreach (var dup in duplicateStrings)
                    writer.WriteLine($"{FormatHelper.TruncateString(dup.Preview, 50),-50} {dup.Count,10:N0} {FormatHelper.FormatBytes(dup.WastedBytes),12}");
            }

            writer.WriteLine("\nHIGH-REFERENCE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Highly referenced objects: {domain.HighlyReferencedObjectCount:N0}");
            var topHighRefs = domain.TopHighlyReferencedObjects ?? [];
            if (topHighRefs.Count > 0)
            {
                writer.WriteLine("\nTop highly referenced objects:");
                foreach (var obj in topHighRefs)
                {
                    writer.WriteLine($"  {obj.TypeName}");
                    writer.WriteLine($"    Address: 0x{obj.Address:X}");
                    writer.WriteLine($"    Size: {FormatHelper.FormatBytes(obj.Size)}");
                    writer.WriteLine($"    Incoming references: {obj.IncomingReferences:N0}");
                    writer.WriteLine(string.Empty);
                }
            }

            if (domain.SkippedReferenceAddresses > 0)
                writer.WriteLine($"Reference tracking cap hit; skipped addresses: {domain.SkippedReferenceAddresses:N0}");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
