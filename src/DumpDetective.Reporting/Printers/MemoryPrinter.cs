using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class MemoryPrinter : IAnalyzerReporter
    {
        private const int TopItemsToShow = 20;

        public string AnalyzerName => "Memory Analysis";
        public string DisplayTitle => "Memory Analysis";
        public int SortOrder => 20;

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not MemoryDomainResult domain)
                return;

            writer.WriteHeader("MEMORY ANALYSIS:");
            writer.WriteSubHeading("OVERALL SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Memory", FormatHelper.FormatBytes(domain.TotalBytes));
            writer.WriteMetric("Total Objects", $"{domain.TotalObjects:N0}");
            writer.WriteMetric("LOH Memory", $"{FormatHelper.FormatBytes(domain.LohBytes)} ({domain.LohPercent:F1}%)");
            writer.WriteMetric("LOH Objects", $"{domain.LohObjects:N0} ({domain.LohPercent:F1}% of total memory)");
            writer.WriteMetric("LOH Threshold", $"{domain.LohThresholdBytes:N0} bytes");
            writer.WriteMetric("Unique Types", $"{domain.UniqueTypes:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HEAP COMPOSITION SIGNALS:");
            writer.WriteSeparator();
            if (domain.LohPercent >= 40)
                writer.WriteDetailText("⚠️  LOH share is elevated; review large-object allocation and retention patterns.");
            else
                writer.WriteDetailText("✅ LOH share appears within expected range for this snapshot.");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP 20 OBJECT TYPES BY MEMORY SIZE:");
            writer.WriteSeparator();
            writer.WriteDetailTable(new DetailedAnalyzerTableData(
                Caption: "Top 20 object types by memory size",
                Headers: ["Type", "Count", "Total Size"],
                Rows: domain.TopTypesBySize.Take(TopItemsToShow)
                    .Select(t => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(t.TypeName),
                        new DetailedAnalyzerTableCell($"{t.Count:N0}", t.Count),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(t.TotalBytes), (long)t.TotalBytes)]))
                    .ToList()));

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP 20 OBJECT TYPES BY COUNT:");
            writer.WriteSeparator();
            writer.WriteDetailTable(new DetailedAnalyzerTableData(
                Caption: "Top 20 object types by count",
                Headers: ["Type", "Count", "Total Size"],
                Rows: domain.TopTypesByCount.Take(TopItemsToShow)
                    .Select(t => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(t.TypeName),
                        new DetailedAnalyzerTableCell($"{t.Count:N0}", t.Count),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(t.TotalBytes), (long)t.TotalBytes)]))
                    .ToList()));

            writer.WriteDetailDivider();
        }
    }
}
