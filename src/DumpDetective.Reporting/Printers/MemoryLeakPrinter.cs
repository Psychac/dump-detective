using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
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
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Top types in finalizer queue",
                    Headers: ["Type", "Count", "% Queue"],
                    Rows: finalizerTypes.Select(t =>
                    {
                        double pct = domain.FinalizerQueueCount == 0 ? 0 : t.Count * 100.0 / domain.FinalizerQueueCount;
                        return new DetailedAnalyzerTableRow([
                            new DetailedAnalyzerTableCell(t.Name),
                            new DetailedAnalyzerTableCell($"{t.Count:N0}", t.Count),
                            new DetailedAnalyzerTableCell($"{pct:F1}%")]);
                    }).ToList()));
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
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Most duplicated strings",
                    Headers: ["String Preview", "Count", "Wasted"],
                    Rows: duplicateStrings.Select(dup => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(FormatHelper.TruncateString(dup.Preview, 80)),
                        new DetailedAnalyzerTableCell($"{dup.Count:N0}", dup.Count),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes)]))
                    .ToList()));
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



