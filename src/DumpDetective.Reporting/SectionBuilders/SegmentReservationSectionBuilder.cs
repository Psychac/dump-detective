using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class SegmentReservationSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopSegmentsToShow = 15;

    public string AnalyzerName => "Segment Reservation Analysis";
    public int SortOrder => 36;

    public bool CanHandle(AnalyzerDomainResult result) => result is SegmentReservationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SegmentReservationDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── §25.1 Committed vs Reserved ──────────────────────────────────────
        blocks.Add(H("COMMITTED VS RESERVED MEMORY"));
        blocks.Add(Divider());
        blocks.Add(M("Total committed",           FormatHelper.FormatBytes(d.TotalCommittedBytes),  (double)d.TotalCommittedBytes));
        blocks.Add(M("Total reserved",            FormatHelper.FormatBytes(d.TotalReservedBytes),   (double)d.TotalReservedBytes));
        blocks.Add(M("Reservation gap",           FormatHelper.FormatBytes(d.ReservationGapBytes),  (double)d.ReservationGapBytes));
        blocks.Add(M("Reserved-to-committed ratio", $"{d.ReservedToCommittedRatio:F2}x",            d.ReservedToCommittedRatio));

        // ── §25.2 Segment Lifecycle ───────────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("SEGMENT LIFECYCLE"));
        blocks.Add(Divider());
        blocks.Add(M("Ephemeral segments",          $"{d.EphemeralSegmentCount:N0}",              d.EphemeralSegmentCount));
        blocks.Add(M("Avg ephemeral fill",          $"{d.AvgEphemeralFillPct:F1} %",              d.AvgEphemeralFillPct));
        blocks.Add(M("Non-ephemeral SOH segments",  $"{d.NonEphemeralSohSegmentCount:N0}",        d.NonEphemeralSohSegmentCount));
        blocks.Add(M("Logical heap count",          $"{d.ReservedByLogicalHeap.Count:N0}",        d.ReservedByLogicalHeap.Count));

        // Per-logical-heap reserved breakdown (server GC).
        if (d.ReservedByLogicalHeap.Count > 1)
        {
            blocks.Add(Blank());
            blocks.Add(H("RESERVED BYTES BY LOGICAL HEAP"));
            blocks.Add(Divider());

            var heapRows = new List<TableRow>(d.ReservedByLogicalHeap.Count);
            foreach (KeyValuePair<int, ulong> kv in d.ReservedByLogicalHeap.OrderBy(x => x.Key))
            {
                heapRows.Add(new TableRow([
                    Cell($"Heap {kv.Key}"),
                    Cell(FormatHelper.FormatBytes(kv.Value), (long)kv.Value)]));
            }
            blocks.Add(new TableBlock("Reserved bytes per logical heap", ["Heap", "Reserved"], heapRows));
        }

        // ── Top segments by reserved size ────────────────────────────────────
        var top = d.SegmentTable
            .OrderByDescending(s => s.ReservedBytes)
            .Take(TopSegmentsToShow)
            .ToList();

        if (top.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H($"TOP {top.Count} SEGMENTS BY RESERVED SIZE"));
            blocks.Add(Divider());

            var segRows = new List<TableRow>(top.Count);
            foreach (SegmentReservationEntry s in top)
            {
                segRows.Add(new TableRow([
                    Cell($"0x{s.Address:x16}"),
                    Cell(s.Kind.ToString()),
                    Cell(s.IsEphemeral ? "Yes" : "No"),
                    Cell($"Heap {s.LogicalHeap}"),
                    Cell(FormatHelper.FormatBytes(s.CommittedBytes), (long)s.CommittedBytes),
                    Cell(FormatHelper.FormatBytes(s.ReservedBytes),  (long)s.ReservedBytes),
                    Cell(s.FillPct > 0 ? $"{s.FillPct:F1} %" : "—")]));
            }
            blocks.Add(new TableBlock(
                "Top segments by reserved size",
                ["Address", "Kind", "Ephemeral", "Logical Heap", "Committed", "Reserved", "Fill %"],
                segRows));
        }

        // ── §25.3 Address Space Pressure ─────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("ADDRESS SPACE PRESSURE"));
        blocks.Add(Divider());
        if (d.AddressSpacePressureRisk)
            blocks.Add(T($"⚠ RISK: {d.PressureRiskReason}"));
        else
            blocks.Add(T("No address space pressure detected."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
