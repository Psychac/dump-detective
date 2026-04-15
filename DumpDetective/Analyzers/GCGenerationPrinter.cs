using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class GCGenerationPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "GC Generation Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not GCGenerationDomainResult domain)
                return;

            writer.WriteHeader("GC GENERATIONS BREAKDOWN:");
            writer.WriteLine("HEAP SUMMARY:");
            writer.WriteSeparator();

            int gen2Objects = Math.Max(0, domain.TotalObjects - domain.LohObjects);
            writer.WriteLine($"Gen2 objects: {gen2Objects:N0}, {FormatHelper.FormatBytes(domain.Gen2Bytes)}");
            writer.WriteLine($"LOH objects: {domain.LohObjects:N0}, {FormatHelper.FormatBytes(domain.LohBytes)}");
            writer.WriteLine($"Total objects: {domain.TotalObjects:N0}");
            writer.WriteLine($"LOH percentage: {domain.LohPercent:F1}%");

            writer.WriteLine("\nGENERATION SPLIT:");
            writer.WriteSeparator();
            writer.WriteLine($"Small/Medium heap bytes: {FormatHelper.FormatBytes(domain.Gen2Bytes)}");
            writer.WriteLine($"Large object heap bytes: {FormatHelper.FormatBytes(domain.LohBytes)}");

            writer.WriteLine("\nLOH RISK SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.LohPercent >= 35
                ? "⚠️  LOH footprint is elevated for this dump."
                : "✅ LOH footprint is not elevated.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
