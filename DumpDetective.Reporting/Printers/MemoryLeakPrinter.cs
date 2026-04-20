using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class MemoryLeakPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Memory Leak Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryLeakDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not MemoryLeakDomainResult domain)
                return;

            writer.WriteHeader("MEMORY LEAK ANALYSIS:");
            writer.WriteLine("FINALIZER QUEUE:");
            writer.WriteSeparator();
            writer.WriteLine($"Finalizer queue objects: {domain.FinalizerQueueCount:N0}");
            var finalizerTypes = domain.TopFinalizerTypes ?? [];
            if (finalizerTypes.Count > 0)
            {
                writer.WriteLine("\nTop types in finalizer queue:");
                writer.WriteLine($"{"Type",-80} {"Count",12} {"% Queue",8}");
                foreach (var type in finalizerTypes)
                {
                    double pct = domain.FinalizerQueueCount == 0
                        ? 0
                        : type.Count * 100.0 / domain.FinalizerQueueCount;

                    var wrappedTypeLines = WrapText(type.Name, 80).ToList();
                    if (wrappedTypeLines.Count == 0)
                        wrappedTypeLines.Add(string.Empty);

                    writer.WriteLine($"{wrappedTypeLines[0],-80} {type.Count,12:N0} {pct,7:F1}%");
                    for (int i = 1; i < wrappedTypeLines.Count; i++)
                        writer.WriteLine($"{wrappedTypeLines[i],-80} {string.Empty,12} {string.Empty,8}");
                }
            }

            writer.WriteLine("\nDUPLICATE STRING ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine($"Total strings: {domain.TotalStrings:N0}");
            writer.WriteLine($"Total string memory: {FormatHelper.FormatBytes(domain.TotalStringMemoryBytes)}");
            writer.WriteLine($"Unique strings: {domain.UniqueStrings:N0}");
            writer.WriteLine(string.Empty);
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

            writer.WriteLine("\nHIGHLY REFERENCED OBJECTS:");
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

        private static IEnumerable<string> WrapText(string? value, int width)
        {
            if (string.IsNullOrWhiteSpace(value) || width <= 0)
            {
                yield return string.Empty;
                yield break;
            }

            var text = value.Trim();
            int index = 0;

            while (index < text.Length)
            {
                int remaining = text.Length - index;
                if (remaining <= width)
                {
                    yield return text[index..];
                    yield break;
                }

                int lastSpace = text.LastIndexOf(' ', index + width, width);
                if (lastSpace <= index)
                {
                    yield return text.Substring(index, width);
                    index += width;
                }
                else
                {
                    yield return text.Substring(index, lastSpace - index).TrimEnd();
                    index = lastSpace + 1;
                }

                while (index < text.Length && text[index] == ' ')
                    index++;
            }
        }
    }
}



