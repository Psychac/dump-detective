using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class CollectionPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Collection Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not CollectionDomainResult domain)
                return;

            writer.WriteHeader("COLLECTION EFFICIENCY ANALYSIS:");
            writer.WriteLine("COLLECTION MIX:");
            writer.WriteSeparator();
            writer.WriteLine($"Total collections analyzed: {domain.TotalCollections:N0}");
            writer.WriteLine($"Dictionaries: {domain.Dictionaries:N0}, Lists: {domain.Lists:N0}, HashSets: {domain.HashSets:N0}");

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
