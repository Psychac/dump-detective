using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class LohFragmentationPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "LOH Fragmentation Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not LohFragmentationDomainResult domain)
                return;

            writer.WriteHeader("LOH FRAGMENTATION ANALYSIS:");
            writer.WriteLine("LOH SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"LOH segments: {domain.SegmentCount:N0}");
            writer.WriteLine($"Total LOH bytes: {FormatHelper.FormatBytes(domain.TotalBytes)}");
            writer.WriteLine($"LOH used size: {FormatHelper.FormatBytes(domain.UsedBytes)}");
            writer.WriteLine($"Free LOH bytes: {FormatHelper.FormatBytes(domain.FreeBytes)}");
            writer.WriteLine($"LOH free blocks: {domain.FreeBlockCount:N0}");

            writer.WriteLine("\nFRAGMENTATION SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Fragmentation: {domain.FragmentationPercent:F1}%");
            writer.WriteLine($"Largest free block: {FormatHelper.FormatBytes(domain.LargestFreeBlock)}");
            writer.WriteLine(domain.FragmentationPercent >= 35
                ? "⚠️  LOH fragmentation appears elevated."
                : "✅ LOH fragmentation appears acceptable.");

            writer.WriteLine("\nTOP FRAGMENTED LOH SEGMENTS:");
            writer.WriteSeparator();
            var segments = domain.TopFragmentedSegments ?? [];
            if (segments.Count == 0)
            {
                writer.WriteLine("No segment-level fragmentation details available.");
            }
            else
            {
                foreach (var seg in segments.Take(8))
                {
                    writer.WriteLine($"  • 0x{seg.Address:X}: {seg.FragmentationPercent:F1}% frag, free {FormatHelper.FormatBytes(seg.FreeBytes)}, largest hole {FormatHelper.FormatBytes(seg.LargestFreeBlock)}");
                }
            }
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
