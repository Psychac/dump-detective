using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class CollectionPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Collection Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not CollectionDomainResult domain)
                return;

            writer.WriteHeader("COLLECTION EFFICIENCY ANALYSIS:");
            writer.WriteSubHeading("COLLECTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Collections", $"{domain.TotalCollections:N0}");
            writer.WriteMetric("Dictionaries", $"{domain.Dictionaries:N0}", indentLevel: 1);
            writer.WriteMetric("Lists", $"{domain.Lists:N0}", indentLevel: 1);
            writer.WriteMetric("HashSets", $"{domain.HashSets:N0}", indentLevel: 1);
            writer.WriteMetric("Queues", $"{domain.Queues:N0}", indentLevel: 1);
            writer.WriteDetailBlank();
            writer.WriteMetric("Total Wasted Memory", FormatHelper.FormatBytes(domain.TotalWastedMemory));

            var topWasteful = domain.TopWastefulCollections ?? [];
            if (topWasteful.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("MOST WASTEFUL COLLECTIONS (Top 15):");
                writer.WriteDetailText($"{"Type",-50} {"Count/Capacity",15} {"Fill Rate",10} {"Wasted",12}");
                foreach (var entry in topWasteful)
                {
                    writer.WriteDetailText($"{FormatHelper.TruncateString(entry.Type, 50),-50} {($"{entry.Count}/{entry.Capacity}"),15} {($"{entry.FillRate:F1}%"),10} {FormatHelper.FormatBytes(entry.WastedMemory),12}");
                    writer.WriteMetric("Address", $"0x{entry.Address:X}", indentLevel: 1);
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("WASTE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteMetric("Wasteful collections", $"{domain.WastefulCollectionCount:N0}");
            writer.WriteMetric("Estimated unused capacity", FormatHelper.FormatBytes(domain.TotalWastedMemory));

            writer.WriteDetailBlank();
            writer.WriteSubHeading("CAPACITY RECOMMENDATION:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.TotalWastedMemory >= 10UL * 1024 * 1024
                ? "⚠️  Consider trimming long-lived collections or setting more accurate initial capacities."
                : "✅ Collection sizing appears acceptable for this snapshot.");
            writer.WriteDetailDivider();
        }
    }
}
