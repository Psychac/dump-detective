using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCPressureSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.gc-pressure";
    public string DisplayTitle => "GC & Allocation Pressure";
    public int SortOrder => 1400;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<GCGenerationDomainResult>() is not null
        || results.Get<AllocationPatternDomainResult>() is not null
        || results.Get<SegmentAnalysisDomainResult>() is not null
        || results.Get<SegmentReservationDomainResult>() is not null
        || results.Get<GCHandleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        GCGenerationDomainResult? gcGen = results.Get<GCGenerationDomainResult>();
        AllocationPatternDomainResult? allocation = results.Get<AllocationPatternDomainResult>();
        SegmentAnalysisDomainResult? segments = results.Get<SegmentAnalysisDomainResult>();
        SegmentReservationDomainResult? reservation = results.Get<SegmentReservationDomainResult>();
        GCHandleDomainResult? handles = results.Get<GCHandleDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("GC PRESSURE SNAPSHOT"),
            T("Generation pressure and segment pressure are combined here so the report can show one concise GC health view."),
        };

        blocks.Add(new TableBlock(
            Caption: "Pressure signals",
            Headers: ["Signal", "Value", "Interpretation"],
            Rows:
            [
                Row(Cell("GC pressure level"), Cell(allocation?.GCPressure.ToString() ?? "N/A"), Cell(DescribeGcPressure(allocation))),
                Row(Cell("Gen2 share"), Cell(gcGen is null ? "N/A" : $"{gcGen.Gen2Pct:F1}%"), Cell(gcGen is null ? "No GC generation data available." : DescribeGen2(gcGen.Gen2Pct))),
                Row(Cell("LOH share"), Cell(gcGen is null ? "N/A" : $"{gcGen.LohPercent:F1}%"), Cell(gcGen is null ? "No LOH data available." : DescribeLoh(gcGen.LohPercent))),
                Row(Cell("Promotion pressure"), Cell(allocation is null ? "N/A" : $"{allocation.PromotionPressureScore:F1}"), Cell(allocation is null ? "No allocation pressure data available." : DescribePromotion(allocation.PromotionPressureScore))),
                Row(Cell("Reserved / committed"), Cell(reservation is null ? "N/A" : $"{reservation.ReservedToCommittedRatio:F2}x"), Cell(reservation is null ? "No segment reservation data available." : DescribeReservation(reservation))),
                Row(Cell("Ephemeral fill"), Cell(reservation is null ? "N/A" : $"{reservation.AvgEphemeralFillPct:F1}%"), Cell(reservation is null ? "No ephemeral segment data available." : DescribeEphemeralFill(reservation.AvgEphemeralFillPct))),
                Row(Cell("Pinned handles"), Cell(handles is null ? "N/A" : $"{handles.PinnedHandleTargets:N0}"), Cell(handles is null ? "No GC handle data available." : DescribePinnedHandles(handles.PinnedHandleTargets))),
            ]));

        blocks.Add(Blank());
        blocks.Add(H("SEGMENT FOOTPRINT"));
        if (segments is null)
        {
            blocks.Add(T("No segment analysis result was available."));
        }
        else
        {
            blocks.Add(M("Committed bytes", FormatBytes(segments.TotalCommittedBytes), (double)segments.TotalCommittedBytes));
            blocks.Add(M("Used bytes", FormatBytes(segments.TotalUsedBytes), (double)segments.TotalUsedBytes));
            double utilization = segments.TotalCommittedBytes == 0 ? 0.0 : segments.TotalUsedBytes * 100.0 / segments.TotalCommittedBytes;
            blocks.Add(M("Utilization", $"{utilization:F1}%", utilization));
            blocks.Add(M("Reserved bytes", FormatBytes(segments.TotalReservedBytes), (double)segments.TotalReservedBytes));
            blocks.Add(M("Reservation gap", FormatBytes(segments.ReservationGapBytes), (double)segments.ReservationGapBytes));
            blocks.Add(M("LOH bytes", FormatBytes(segments.LohBytes), (double)segments.LohBytes));
            blocks.Add(M("POH bytes", FormatBytes(segments.PohBytes), (double)segments.PohBytes));
            blocks.Add(M("Frozen bytes", FormatBytes(segments.FrozenBytes), (double)segments.FrozenBytes));
        }

        if (gcGen is not null && gcGen.TopLohTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP LOH OBJECT TYPES"));
            var rows = new List<TableRow>(Math.Min(gcGen.TopLohTypes.Count, 10));
            int limit = Math.Min(gcGen.TopLohTypes.Count, 10);
            for (int i = 0; i < limit; i++)
            {
                TypeSnapshot type = gcGen.TopLohTypes[i];
                rows.Add(Row(
                    Cell(type.TypeName),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.LohBytes > 0 ? FormatBytes(type.LohBytes) : "—")));
            }

            blocks.Add(new TableBlock(
                Caption: "Top LOH object types",
                Headers: ["Type", "Count", "Total Size", "LOH Bytes"],
                Rows: rows));
        }

        blocks.Add(Blank());
        blocks.Add(H("ALLOCATOR NOTES"));
        blocks.Add(T("Allocation-site precision is ETW-dependent; these signals summarize heap pressure and segment state from the dump only."));

        return new AnalyzerDetailSection(
            AnalyzerName: "GC & Allocation Pressure",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static string DescribeGcPressure(AllocationPatternDomainResult? allocation)
    {
        if (allocation is null)
            return "No allocation-pattern result was available.";

        return allocation.GCPressure switch
        {
            GCPressureLevel.Critical => "Critical GC pressure. Large Gen2/LOH retention is already present.",
            GCPressureLevel.High => "High GC pressure. Gen2 retention is elevated and should be investigated.",
            GCPressureLevel.Moderate => "Moderate GC pressure. Monitor for continued growth.",
            _ => "GC pressure is within normal bounds."
        };
    }

    private static string DescribeGen2(double gen2Pct)
        => gen2Pct >= 40.0 ? "Gen2 dominates the heap; retention is likely becoming long-lived." : "Gen2 share is not yet dominant.";

    private static string DescribeLoh(double lohPct)
        => lohPct >= 35.0 ? "LOH share is elevated and may be contributing to fragmentation or promotion pressure." : "LOH share is within the lower-risk band.";

    private static string DescribePromotion(double score)
        => score >= 80.0 ? "Promotion pressure is severe." : score >= 50.0 ? "Promotion pressure is moderate." : "Promotion pressure is low.";

    private static string DescribeReservation(SegmentReservationDomainResult reservation)
        => reservation.AddressSpacePressureRisk
            ? $"Address-space pressure risk flagged: {reservation.PressureRiskReason}"
            : $"Reservation gap remains within the current expected band ({reservation.ReservedToCommittedRatio:F2}x).";

    private static string DescribeEphemeralFill(double fillPct)
        => fillPct >= 90.0 ? "Ephemeral segments are close to saturation." : fillPct >= 80.0 ? "Ephemeral segments are under noticeable pressure." : "Ephemeral fill is not yet a pressure concern.";

    private static string DescribePinnedHandles(int pinnedTargets)
        => pinnedTargets > 0 ? $"Pinned handle targets are present ({pinnedTargets:N0}); verify they are expected." : "No pinned handle target pressure is visible.";

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}