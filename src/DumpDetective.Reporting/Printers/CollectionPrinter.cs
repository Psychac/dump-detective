using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

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
            writer.WriteLine("COLLECTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Collections: {domain.TotalCollections:N0}");
            writer.WriteLine($"  Dictionaries: {domain.Dictionaries:N0}");
            writer.WriteLine($"  Lists: {domain.Lists:N0}");
            writer.WriteLine($"  HashSets: {domain.HashSets:N0}");
            writer.WriteLine($"  Queues: {domain.Queues:N0}");
            writer.WriteLine(string.Empty);
            writer.WriteLine($"Total Wasted Memory: {FormatHelper.FormatBytes(domain.TotalWastedMemory)}");

            var topWasteful = domain.TopWastefulCollections ?? [];
            if (topWasteful.Count > 0)
            {
                writer.WriteLine("\nMOST WASTEFUL COLLECTIONS (Top 15):");
                writer.WriteLine($"{"Type",-50} {"Count/Capacity",15} {"Fill Rate",10} {"Wasted",12}");
                foreach (var entry in topWasteful)
                {
                    writer.WriteLine($"{FormatHelper.TruncateString(entry.Type, 50),-50} {($"{entry.Count}/{entry.Capacity}"),15} {($"{entry.FillRate:F1}%"),10} {FormatHelper.FormatBytes(entry.WastedMemory),12}");
                    writer.WriteLine($"  Address: 0x{entry.Address:X}");
                }
            }

            writer.WriteLine("\nWASTE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Wasteful collections: {domain.WastefulCollectionCount:N0}");
            writer.WriteLine($"Estimated unused capacity: {FormatHelper.FormatBytes(domain.TotalWastedMemory)}");

            writer.WriteLine("\nCAPACITY RECOMMENDATION:");
            writer.WriteSeparator();
            writer.WriteLine(domain.TotalWastedMemory >= 10UL * 1024 * 1024
                ? "⚠️  Consider trimming long-lived collections or setting more accurate initial capacities."
                : "✅ Collection sizing appears acceptable for this snapshot.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
