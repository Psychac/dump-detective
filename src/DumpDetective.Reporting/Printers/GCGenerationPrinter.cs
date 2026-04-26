using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class GCGenerationPrinter : IAnalyzerReporter
    {
        private const int TopLohTypesToShow = 15;

        public string AnalyzerName => "GC Generation Analysis";
        public string DisplayTitle => "GC Generation Breakdown";
        public int SortOrder => 50;

        public bool CanHandle(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not GCGenerationDomainResult domain)
                return;

            writer.WriteHeader("GC GENERATIONS BREAKDOWN:");
            writer.WriteSubHeading("HEAP SUMMARY:");
            writer.WriteSeparator();

            writer.WriteMetric("Gen0 objects", $"{domain.Gen0Objects:N0}, {FormatHelper.FormatBytes(domain.Gen0Bytes)}");
            writer.WriteMetric("Gen1 objects", $"{domain.Gen1Objects:N0}, {FormatHelper.FormatBytes(domain.Gen1Bytes)}");
            writer.WriteMetric("Gen2 objects", $"{domain.Gen2Objects:N0}, {FormatHelper.FormatBytes(domain.Gen2Bytes)}");
            writer.WriteMetric("LOH objects", $"{domain.LohObjects:N0}, {FormatHelper.FormatBytes(domain.LohBytes)}");
            writer.WriteMetric("Total objects", $"{domain.TotalObjects:N0}");
            writer.WriteMetric("LOH percentage", $"{domain.LohPercent:F1}%");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("GENERATION SPLIT:");
            writer.WriteSeparator();
            writer.WriteMetric("Gen0 bytes", FormatHelper.FormatBytes(domain.Gen0Bytes));
            writer.WriteMetric("Gen1 bytes", FormatHelper.FormatBytes(domain.Gen1Bytes));
            writer.WriteMetric("Gen2 bytes", FormatHelper.FormatBytes(domain.Gen2Bytes));
            writer.WriteMetric("Large object heap bytes", FormatHelper.FormatBytes(domain.LohBytes));

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LARGE OBJECT HEAP (LOH) USAGE:");
            writer.WriteSeparator();
            writer.WriteMetric("Total LOH Objects", $"{domain.LohObjects:N0}");
            writer.WriteMetric("Total LOH Size", FormatHelper.FormatBytes(domain.LohBytes));

            var topLohTypes = domain.TopLohTypes ?? [];
            if (topLohTypes.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("Top LOH Object Types:");
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Top LOH object types",
                    Headers: ["Type", "Count", "Total Size"],
                    Rows: topLohTypes.Take(TopLohTypesToShow)
                        .Select(t => new DetailedAnalyzerTableRow([
                            new DetailedAnalyzerTableCell(t.TypeName),
                            new DetailedAnalyzerTableCell($"{t.Count:N0}", t.Count),
                            new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(t.TotalBytes), (long)t.TotalBytes)]))
                        .ToList()));
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LOH RISK SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.LohPercent >= 35
                ? "⚠️  LOH footprint is elevated for this dump."
                : "✅ LOH footprint is not elevated.");

            writer.WriteDetailDivider();
        }

            }
        }



