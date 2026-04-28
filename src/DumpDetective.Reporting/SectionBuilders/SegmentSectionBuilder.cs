using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class SegmentSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopSegmentsToShow = 10;

    public string AnalyzerName => "Segment Analysis";
    public int SortOrder => 35;

    public bool CanHandle(AnalyzerDomainResult result) => result is SegmentAnalysisDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SegmentAnalysisDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("SEGMENT SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total segments",  $"{d.TotalSegments:N0}",                             d.TotalSegments));
        blocks.Add(M("Total committed", FormatHelper.FormatBytes(d.TotalCommittedBytes),      (double)d.TotalCommittedBytes));
        blocks.Add(Blank());
        blocks.Add(M("SOH segments",    $"{d.SohSegmentCount:N0}  ({FormatHelper.FormatBytes(d.SohBytes)})",                        d.SohSegmentCount));
        blocks.Add(M("LOH segments",    $"{d.LohSegmentCount:N0}  ({FormatHelper.FormatBytes(d.LohBytes)}, {d.LohPercent:F1}%)",     d.LohSegmentCount));
        blocks.Add(M("POH segments",    $"{d.PohSegmentCount:N0}  ({FormatHelper.FormatBytes(d.PohBytes)}, {d.PohPercent:F1}%)",     d.PohSegmentCount));
        if (d.FrozenSegmentCount > 0)
            blocks.Add(M("Frozen segments", $"{d.FrozenSegmentCount:N0}  ({FormatHelper.FormatBytes(d.FrozenBytes)})", d.FrozenSegmentCount));

        blocks.Add(Blank());
        blocks.Add(H("PER-KIND BREAKDOWN"));
        blocks.Add(Divider());

        var kindRows = new List<TableRow>();
        foreach (var k in d.KindSummaries)
        {
            if (k.SegmentCount == 0) continue;
            kindRows.Add(new TableRow([
                Cell(k.Kind.ToString()),
                Cell($"{k.SegmentCount:N0}", k.SegmentCount),
                Cell($"{k.ObjectCount:N0}",  k.ObjectCount),
                Cell(FormatHelper.FormatBytes(k.TotalBytes), (long)k.TotalBytes)]));
        }
        blocks.Add(new TableBlock("Segment kind breakdown", ["Kind", "Segments", "Objects", "Committed"], kindRows));

        var topSegments = d.TopSegmentsBySize ?? [];
        if (topSegments.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H($"TOP {Math.Min(topSegments.Count, TopSegmentsToShow)} SEGMENTS BY SIZE"));
            blocks.Add(Divider());

            var segRows = new List<TableRow>(Math.Min(topSegments.Count, TopSegmentsToShow));
            int limit = Math.Min(topSegments.Count, TopSegmentsToShow);
            for (int i = 0; i < limit; i++)
            {
                var s = topSegments[i];
                segRows.Add(new TableRow([
                    Cell($"0x{s.Address:x16}"),
                    Cell(s.Kind.ToString()),
                    Cell(FormatHelper.FormatBytes(s.CommittedBytes), (long)s.CommittedBytes),
                    Cell($"{s.ObjectCount:N0}", s.ObjectCount)]));
            }
            blocks.Add(new TableBlock("Top segments by committed size", ["Address", "Kind", "Committed", "Objects"], segRows));
        }

        blocks.Add(Blank());
        blocks.Add(H("SEGMENT HEALTH SIGNAL"));
        blocks.Add(Divider());
        if (d.LohPercent >= 40)
            blocks.Add(T("LOH is critically large — exceeds 40% of committed heap."));
        else if (d.LohPercent >= 20)
            blocks.Add(T("LOH share is elevated — monitor large-object allocation patterns."));
        else
            blocks.Add(T("Segment distribution appears normal for this dump."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
