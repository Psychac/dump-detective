using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LohFragmentationSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "LOH Fragmentation Analysis";
    public int SortOrder => 55;

    public bool CanHandle(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (LohFragmentationDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("LOH FRAGMENTATION SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total LOH Bytes",         FormatHelper.FormatBytes(d.TotalBytes),     (double)d.TotalBytes));
        blocks.Add(M("Segment Count",           $"{d.SegmentCount:N0}",                     d.SegmentCount));
        blocks.Add(M("Overall Fragmentation",   $"{d.FragmentationPercent:F1}%",            d.FragmentationPercent));
        blocks.Add(M("Free Bytes",              FormatHelper.FormatBytes(d.FreeBytes),       (double)d.FreeBytes));
        blocks.Add(M("Free Blocks",             $"{d.FreeBlockCount:N0}",                   d.FreeBlockCount));
        blocks.Add(M("Largest Free Block",      FormatHelper.FormatBytes(d.LargestFreeBlock),(double)d.LargestFreeBlock));

        var segments = d.TopFragmentedSegments ?? [];
        if (segments.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("PER-SEGMENT BREAKDOWN"));
            blocks.Add(Divider());

            var segRows = new List<TableRow>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                segRows.Add(new TableRow([
                    Cell($"0x{s.Address:x16}"),
                    Cell(FormatHelper.FormatBytes(d.TotalBytes / (ulong)Math.Max(1, d.SegmentCount))), // approx segment size
                    Cell($"{s.FragmentationPercent:F1}%", (long)(s.FragmentationPercent * 100)),
                    Cell(FormatHelper.FormatBytes(s.LargestFreeBlock), (long)s.LargestFreeBlock)]));
            }
            blocks.Add(new TableBlock("Top fragmented segments", ["Address", "Size", "Frag %", "Largest Free Block"], segRows));
        }

        blocks.Add(Blank());
        blocks.Add(H("FRAGMENTATION SIGNAL"));
        blocks.Add(Divider());
        if (d.FragmentationPercent >= 40)
            blocks.Add(T("LOH fragmentation is critically high — compaction or large-object pooling recommended."));
        else if (d.FragmentationPercent >= 20)
            blocks.Add(T("LOH fragmentation is elevated — monitor allocation patterns."));
        else
            blocks.Add(T("LOH fragmentation is within normal range."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
