using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class LohFragmentationPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "LOH Fragmentation Analysis";
        public string DisplayTitle => "LOH Fragmentation Analysis";
        public int SortOrder => 40;

        public bool CanHandle(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not LohFragmentationDomainResult domain)
                return;

            writer.WriteHeader("LOH FRAGMENTATION ANALYSIS:");
            writer.WriteSubHeading("LOH SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("LOH segments", $"{domain.SegmentCount:N0}");
            writer.WriteMetric("Total LOH bytes", FormatHelper.FormatBytes(domain.TotalBytes));
            writer.WriteMetric("LOH used size", FormatHelper.FormatBytes(domain.UsedBytes));
            writer.WriteMetric("Free LOH bytes", FormatHelper.FormatBytes(domain.FreeBytes));
            writer.WriteMetric("LOH free blocks", $"{domain.FreeBlockCount:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("FRAGMENTATION SIGNAL:");
            writer.WriteSeparator();
            writer.WriteMetric("Fragmentation", $"{domain.FragmentationPercent:F1}%");
            writer.WriteMetric("Largest free block", FormatHelper.FormatBytes(domain.LargestFreeBlock));
            writer.WriteDetailText(domain.FragmentationPercent >= 35
                ? "⚠️  LOH fragmentation appears elevated."
                : "✅ LOH fragmentation appears acceptable.");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP FRAGMENTED LOH SEGMENTS:");
            writer.WriteSeparator();
            var segments = domain.TopFragmentedSegments ?? [];
            if (segments.Count == 0)
            {
                writer.WriteDetailText("No segment-level fragmentation details available.");
            }
            else
            {
                foreach (var seg in segments.Take(8))
                {
                    writer.WriteDetailText($"• 0x{seg.Address:X}: {seg.FragmentationPercent:F1}% frag, free {FormatHelper.FormatBytes(seg.FreeBytes)}, largest hole {FormatHelper.FormatBytes(seg.LargestFreeBlock)}", indentLevel: 1);
                }
            }
            writer.WriteDetailDivider();
        }
    }
}



