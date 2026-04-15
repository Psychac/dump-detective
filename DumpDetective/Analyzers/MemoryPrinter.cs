using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class MemoryPrinter : IAnalyzerReporter
    {
        private const int TopItemsToShow = 10;

        public string AnalyzerName => "Memory Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not MemoryDomainResult domain)
                return;

            writer.WriteHeader("MEMORY ANALYSIS:");
            writer.WriteLine("OVERALL SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Memory: {FormatHelper.FormatBytes(domain.TotalBytes)}");
            writer.WriteLine($"LOH Memory: {FormatHelper.FormatBytes(domain.LohBytes)} ({domain.LohPercent:F1}%)");
            writer.WriteLine($"Unique Types: {domain.UniqueTypes:N0}");

            writer.WriteLine("\nHEAP COMPOSITION SIGNALS:");
            writer.WriteSeparator();
            if (domain.LohPercent >= 40)
                writer.WriteLine("⚠️  LOH share is elevated; review large-object allocation and retention patterns.");
            else
                writer.WriteLine("✅ LOH share appears within expected range for this snapshot.");

            writer.WriteLine("\nTOP TYPES BY MEMORY SIZE:");
            writer.WriteSeparator();
            int shown = 0;
            foreach (var type in domain.TopTypesBySize)
            {
                if (shown >= TopItemsToShow)
                    break;

                writer.WriteLine($"  • {FormatHelper.TruncateString(type.TypeName, 80)} — {FormatHelper.FormatBytes(type.TotalBytes)} across {type.Count:N0} objects");
                shown++;
            }

            writer.WriteLine("\nTOP TYPES BY OBJECT COUNT:");
            writer.WriteSeparator();
            shown = 0;
            foreach (var type in domain.TopTypesByCount)
            {
                if (shown >= TopItemsToShow)
                    break;

                writer.WriteLine($"  • {FormatHelper.TruncateString(type.TypeName, 80)} — {type.Count:N0} objects, {FormatHelper.FormatBytes(type.TotalBytes)}");
                shown++;
            }

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
