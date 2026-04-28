using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers;

internal sealed class SegmentPrinter : IAnalyzerReporter
{
    private const int TopSegmentsToShow = 10;

    public string AnalyzerName => "Segment Analysis";
    public string DisplayTitle => "Heap Segment Distribution";
    public int SortOrder => 45;

    public bool CanHandle(AnalyzerDomainResult result) => result is SegmentAnalysisDomainResult;

    public void Render(AnalyzerDomainResult result, IReportWriter writer)
    {
        if (result is not SegmentAnalysisDomainResult domain)
            return;

        writer.WriteHeader("HEAP SEGMENT DISTRIBUTION:");
        writer.WriteSubHeading("SEGMENT SUMMARY:");
        writer.WriteSeparator();

        writer.WriteMetric("Total segments", $"{domain.TotalSegments:N0}");
        writer.WriteMetric("Total committed", FormatHelper.FormatBytes(domain.TotalCommittedBytes));
        writer.WriteDetailBlank();

        writer.WriteMetric("SOH segments", $"{domain.SohSegmentCount:N0}  ({FormatHelper.FormatBytes(domain.SohBytes)})");
        writer.WriteMetric("LOH segments", $"{domain.LohSegmentCount:N0}  ({FormatHelper.FormatBytes(domain.LohBytes)}, {domain.LohPercent:F1}%)");
        writer.WriteMetric("POH segments", $"{domain.PohSegmentCount:N0}  ({FormatHelper.FormatBytes(domain.PohBytes)}, {domain.PohPercent:F1}%)");

        if (domain.FrozenSegmentCount > 0)
            writer.WriteMetric("Frozen segments", $"{domain.FrozenSegmentCount:N0}  ({FormatHelper.FormatBytes(domain.FrozenBytes)})");

        writer.WriteDetailBlank();
        writer.WriteSubHeading("PER-KIND BREAKDOWN:");
        writer.WriteSeparator();

        writer.WriteDetailTable(new DetailedAnalyzerTableData(
            Caption: "Segment kind breakdown",
            Headers: ["Kind", "Segments", "Objects", "Committed"],
            Rows: domain.KindSummaries
                .Where(k => k.SegmentCount > 0)
                .OrderByDescending(k => k.TotalBytes)
                .Select(k => new DetailedAnalyzerTableRow([
                    new DetailedAnalyzerTableCell(k.Kind.ToString()),
                    new DetailedAnalyzerTableCell($"{k.SegmentCount:N0}", k.SegmentCount),
                    new DetailedAnalyzerTableCell($"{k.ObjectCount:N0}", k.ObjectCount),
                    new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(k.TotalBytes), (long)k.TotalBytes)]))
                .ToList()));

        var topSegments = domain.TopSegmentsBySize ?? [];
        if (topSegments.Count > 0)
        {
            writer.WriteDetailBlank();
            writer.WriteSubHeading($"TOP {Math.Min(topSegments.Count, TopSegmentsToShow)} SEGMENTS BY SIZE:");
            writer.WriteSeparator();

            writer.WriteDetailTable(new DetailedAnalyzerTableData(
                Caption: "Top segments by committed size",
                Headers: ["Address", "Kind", "Committed", "Objects"],
                Rows: topSegments.Take(TopSegmentsToShow)
                    .Select(s => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell($"0x{s.Address:x16}"),
                        new DetailedAnalyzerTableCell(s.Kind.ToString()),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(s.CommittedBytes), (long)s.CommittedBytes),
                        new DetailedAnalyzerTableCell($"{s.ObjectCount:N0}", s.ObjectCount)]))
                    .ToList()));
        }

        writer.WriteDetailBlank();
        writer.WriteSubHeading("SEGMENT HEALTH SIGNAL:");
        writer.WriteSeparator();
        if (domain.LohPercent >= 40)
            writer.WriteDetailText("🔴 LOH is critically large — exceeds 40% of committed heap.");
        else if (domain.LohPercent >= 25)
            writer.WriteDetailText("⚠️  LOH footprint is elevated.");
        else
            writer.WriteDetailText("✅ Segment distribution appears within expected range.");

        if (domain.PohPercent >= 10)
            writer.WriteDetailText($"⚠️  POH occupies {domain.PohPercent:F1}% of committed heap — review pinned allocations.");

        writer.WriteDetailDivider();
    }
}
