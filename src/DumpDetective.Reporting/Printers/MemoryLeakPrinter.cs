using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class MemoryLeakPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Memory Leak Analysis";
        public string DisplayTitle => "Memory Leak Analysis";
        public int SortOrder => 30;

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryLeakDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not MemoryLeakDomainResult domain)
                return;

            writer.WriteHeader("MEMORY LEAK ANALYSIS:");
            writer.WriteSubHeading("FINALIZER QUEUE:");
            writer.WriteSeparator();
            writer.WriteMetric("Finalizer queue objects", $"{domain.FinalizerQueueCount:N0}");
            var finalizerTypes = domain.TopFinalizerTypes ?? [];
            if (finalizerTypes.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("Top types in finalizer queue:");
                writer.WriteDetailText($"{"Type",-80} {"Count",12} {"% Queue",8}");
                foreach (var type in finalizerTypes)
                {
                    double pct = domain.FinalizerQueueCount == 0
                        ? 0
                        : type.Count * 100.0 / domain.FinalizerQueueCount;

                    IReadOnlyList<string> wrappedTypeLines = TableWrapHelper.Wrap(type.Name, 80);
                    writer.WriteDetailText($"{wrappedTypeLines[0],-80} {type.Count,12:N0} {pct,7:F1}%");
                    for (int i = 1; i < wrappedTypeLines.Count; i++)
                        writer.WriteDetailText($"{wrappedTypeLines[i],-80} {string.Empty,12} {string.Empty,8}");
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DUPLICATE STRING ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteMetric("Total strings", $"{domain.TotalStrings:N0}");
            writer.WriteMetric("Total string memory", FormatHelper.FormatBytes(domain.TotalStringMemoryBytes));
            writer.WriteMetric("Unique strings", $"{domain.UniqueStrings:N0}");
            writer.WriteDetailBlank();
            writer.WriteMetric("Duplicate string patterns", $"{domain.DuplicateStringPatternCount:N0}");
            writer.WriteMetric("Estimated duplicate-string waste", FormatHelper.FormatBytes(domain.DuplicateStringWastedBytes));
            var duplicateStrings = domain.TopDuplicateStrings ?? [];
            if (duplicateStrings.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("Most duplicated strings (potential string pooling opportunities):");
                writer.WriteDetailText($"{"String Preview",-50} {"Count",10} {"Wasted",12}");
                foreach (var dup in duplicateStrings)
                    writer.WriteDetailText($"{FormatHelper.TruncateString(dup.Preview, 50),-50} {dup.Count,10:N0} {FormatHelper.FormatBytes(dup.WastedBytes),12}");
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HIGHLY REFERENCED OBJECTS:");
            writer.WriteSeparator();
            writer.WriteMetric("Highly referenced objects", $"{domain.HighlyReferencedObjectCount:N0}");
            var topHighRefs = domain.TopHighlyReferencedObjects ?? [];
            if (topHighRefs.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("Top highly referenced objects:");
                foreach (var obj in topHighRefs)
                {
                    writer.WriteDetailText(obj.TypeName, indentLevel: 1);
                    writer.WriteMetric("Address", $"0x{obj.Address:X}", indentLevel: 2);
                    writer.WriteMetric("Size", FormatHelper.FormatBytes(obj.Size), indentLevel: 2);
                    writer.WriteMetric("Incoming references", $"{obj.IncomingReferences:N0}", indentLevel: 2);
                    writer.WriteDetailBlank();
                }
            }

            if (domain.SkippedReferenceAddresses > 0)
                writer.WriteMetric("Reference tracking cap hit; skipped addresses", $"{domain.SkippedReferenceAddresses:N0}");

            writer.WriteDetailDivider();
        }

            }
        }



