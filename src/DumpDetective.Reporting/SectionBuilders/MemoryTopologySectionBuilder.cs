using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class MemoryTopologySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    private const int TopTypesToShow = 5;
    private const int TopLohTypesToShow = 10;

    public string SectionId => "prof.memory-topology";
    public string DisplayTitle => "Memory Topology";
    public int SortOrder => 1050;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<MemoryDomainResult>() is not null
        || results.Get<GCGenerationDomainResult>() is not null
        || results.Get<SegmentAnalysisDomainResult>() is not null
        || results.Get<SegmentReservationDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        MemoryDomainResult? memory = results.Get<MemoryDomainResult>();
        GCGenerationDomainResult? gcGen = results.Get<GCGenerationDomainResult>();
        SegmentAnalysisDomainResult? segments = results.Get<SegmentAnalysisDomainResult>();
        SegmentReservationDomainResult? reservation = results.Get<SegmentReservationDomainResult>();
        AnalysisIncidentContext? incident = results.IncidentContext;

        var blocks = new List<SectionBlock>
        {
            H("HEAP COMPOSITION"),
            T("Heap topology and generation pressure summarized across the available analyzers."),
        };

        if (segments is null && gcGen is null && memory is null && reservation is null)
        {
            blocks.Add(T("No memory-topology inputs were available."));
            return new AnalyzerDetailSection("Memory Topology", DisplayTitle, SortOrder, blocks);
        }

        blocks.Add(new TableBlock(
            Caption: "Heap composition snapshot",
            Headers: ["Signal", "Value", "Notes"],
            Rows:
            [
                Row(Cell("Total committed"), Cell(segments is null ? "N/A" : FormatBytes(segments.TotalCommittedBytes)), Cell(segments is null ? "No segment analysis result." : "Committed heap bytes across all segment kinds.")),
                Row(Cell("Total used"), Cell(segments is null ? "N/A" : FormatBytes(segments.TotalUsedBytes)), Cell(segments is null ? "No segment analysis result." : "Bytes occupied by live objects across all segment kinds.")),
                Row(Cell("Used / committed"), Cell(segments is null ? "N/A" : segments.TotalCommittedBytes == 0 ? "0.0%" : $"{(segments.TotalUsedBytes * 100.0 / segments.TotalCommittedBytes):F1}%"), Cell(segments is null ? "No segment analysis result." : "How much of the committed heap is actively occupied.")),
                Row(Cell("Total reserved"), Cell(segments is null ? "N/A" : FormatBytes(segments.TotalReservedBytes)), Cell(segments is null ? "No segment analysis result." : "Reserved address space for heap segments.")),
                Row(Cell("Used / reserved"), Cell(segments is null ? "N/A" : segments.TotalReservedBytes == 0 ? "0.0%" : $"{(segments.TotalUsedBytes * 100.0 / segments.TotalReservedBytes):F1}%"), Cell(segments is null ? "No segment analysis result." : "Live object bytes compared with total reserved segment space.")),
                Row(Cell("Reservation gap"), Cell(segments is null ? "N/A" : FormatBytes(segments.ReservationGapBytes)), Cell(segments is null ? "No segment analysis result." : (segments.ReservationGapBytes > 0 ? "Reserved space exceeds committed space." : "Reserved space matches committed space closely."))),
                Row(Cell("Frozen share"), Cell(segments is null ? "N/A" : $"{segments.FrozenPercent:F1}%"), Cell(segments is null ? "No segment analysis result." : "Frozen heap share of total committed memory.")),
                Row(Cell("LOH bytes"), Cell(memory is null ? (gcGen is null ? "N/A" : FormatBytes(gcGen.LohBytes)) : FormatBytes(memory.LohBytes)), Cell(memory is null ? "Derived from GC generation data when memory result is absent." : "Large object heap footprint.")),
                Row(Cell("LOH share"), Cell(memory is null ? (gcGen is null ? "N/A" : $"{gcGen.LohPercent:F1}%") : $"{memory.LohPercent:F1}%"), Cell("LOH fraction of the managed heap.")),
                Row(Cell("GC mode"), Cell(incident?.GcMode ?? "N/A"), Cell(incident is null ? "No incident context available." : "Workstation vs Server GC reported by the runtime context.")),
                Row(Cell("Server GC heaps"), Cell(incident?.HeapCount is null ? "N/A" : incident.HeapCount.Value.ToString("N0"), incident?.HeapCount), Cell(incident is null ? "No incident context available." : "Logical heap count from the runtime context.")),
                Row(Cell("GC pressure"), Cell(gcGen is null ? "N/A" : gcGen.Gen2Pct > 0 ? $"{gcGen.Gen2Pct:F1}% Gen2" : "Available"), Cell(gcGen is null ? "No GC generation result." : DescribeGcPressure(gcGen.Gen2Pct, gcGen.LohPercent))),
                Row(Cell("Allocation profile"), Cell(segments is null ? (reservation is null ? "N/A" : reservation.AddressSpacePressureRisk ? "Pressure" : "Balanced") : (reservation is null ? "Available" : reservation.AddressSpacePressureRisk ? "Pressure" : "Balanced")), Cell("Higher reserved-to-committed ratios and ephemeral fill point toward pressure.")),
            ]));

        if (segments is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("SEGMENT BREAKDOWN"));

            var rows = new List<TableRow>(segments.KindSummaries.Count);
            for (int i = 0; i < segments.KindSummaries.Count; i++)
            {
                SegmentKindSummary summary = segments.KindSummaries[i];
                if (summary.SegmentCount == 0)
                    continue;

                rows.Add(Row(
                    Cell(summary.Kind.ToString()),
                    Cell(summary.SegmentCount.ToString("N0"), summary.SegmentCount),
                    Cell(summary.ObjectCount < 0 ? "N/A" : summary.ObjectCount.ToString("N0"), summary.ObjectCount < 0 ? null : summary.ObjectCount),
                    Cell(FormatBytes(summary.TotalBytes), (long)Math.Min(summary.TotalBytes, long.MaxValue)),
                    Cell(FormatBytes(summary.ReservedBytes), (long)Math.Min(summary.ReservedBytes, long.MaxValue))));
            }

            blocks.Add(new TableBlock(
                Caption: "Segment breakdown by kind",
                Headers: ["Kind", "Segments", "Objects", "Committed", "Reserved"],
                Rows: rows));

            if (segments.PerLogicalHeapSummaries.Count > 0)
            {
                blocks.Add(Blank());
                blocks.Add(H("LOGICAL HEAP BREAKDOWN"));
                var heapRows = new List<TableRow>(segments.PerLogicalHeapSummaries.Count);
                ulong maxBytes = 0;
                ulong minBytes = ulong.MaxValue;

                for (int i = 0; i < segments.PerLogicalHeapSummaries.Count; i++)
                {
                    PerLogicalHeapSummary summary = segments.PerLogicalHeapSummaries[i];
                    if (summary.Bytes > maxBytes) maxBytes = summary.Bytes;
                    if (summary.Bytes < minBytes) minBytes = summary.Bytes;

                    heapRows.Add(Row(
                        Cell($"Heap {summary.LogicalHeapIndex}"),
                        Cell(summary.SegmentCount.ToString("N0"), summary.SegmentCount),
                        Cell(summary.ObjectCount < 0 ? "N/A" : summary.ObjectCount.ToString("N0"), summary.ObjectCount < 0 ? null : summary.ObjectCount),
                        Cell(FormatBytes(summary.Bytes), (long)Math.Min(summary.Bytes, long.MaxValue))));
                }

                blocks.Add(new TableBlock(
                    Caption: "Per-logical-heap breakdown",
                    Headers: ["Logical Heap", "Segments", "Objects", "Bytes"],
                    Rows: heapRows));

                if (segments.PerLogicalHeapSummaries.Count > 1 && minBytes > 0 && maxBytes > minBytes * 2)
                    blocks.Add(T("⚠ Logical heaps are skewed: the largest heap has more than 2x the bytes of the smallest heap."));
            }
        }

        if (reservation is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("RESERVATION PRESSURE"));
            blocks.Add(M("Reserved-to-committed ratio", $"{reservation.ReservedToCommittedRatio:F2}x", reservation.ReservedToCommittedRatio));
            blocks.Add(M("Ephemeral segments", reservation.EphemeralSegmentCount.ToString("N0"), reservation.EphemeralSegmentCount));
            blocks.Add(M("Avg ephemeral fill", $"{reservation.AvgEphemeralFillPct:F1}%", reservation.AvgEphemeralFillPct));
            blocks.Add(M("Address space pressure", reservation.AddressSpacePressureRisk ? "Yes" : "No", reservation.AddressSpacePressureRisk ? 1.0 : 0.0));
            blocks.Add(T(reservation.AddressSpacePressureRisk
                ? $"⚠ {reservation.PressureRiskReason}"
                : "No address space pressure was detected."));

            if (reservation.ReservedByLogicalHeap.Count > 0)
            {
                var heapRows = new List<TableRow>(reservation.ReservedByLogicalHeap.Count);
                foreach (KeyValuePair<int, ulong> kvp in reservation.ReservedByLogicalHeap.OrderBy(kvp => kvp.Key))
                    heapRows.Add(Row(Cell($"Heap {kvp.Key}"), Cell(FormatBytes(kvp.Value), (long)Math.Min(kvp.Value, long.MaxValue))));

                blocks.Add(new TableBlock(
                    Caption: "Reserved bytes by logical heap",
                    Headers: ["Logical Heap", "Reserved"],
                    Rows: heapRows));
            }
        }

        if (memory is not null)
        {
            if (memory.SizeBucketHistogram is { Count: > 0 })
            {
                blocks.Add(Blank());
                blocks.Add(H("OBJECT SIZE DISTRIBUTION"));

                var histogramRows = new List<TableRow>(memory.SizeBucketHistogram.Count);
                for (int i = 0; i < memory.SizeBucketHistogram.Count; i++)
                {
                    SizeBucketEntry bucket = memory.SizeBucketHistogram[i];
                    histogramRows.Add(Row(
                        Cell(bucket.RangeLabel),
                        Cell(bucket.ObjectCount.ToString("N0"), bucket.ObjectCount),
                        Cell(FormatBytes(bucket.TotalBytes), (long)Math.Min(bucket.TotalBytes, long.MaxValue))));
                }

                blocks.Add(new TableBlock(
                    Caption: "Object size histogram",
                    Headers: ["Bucket", "Objects", "Bytes"],
                    Rows: histogramRows));
            }

            blocks.Add(Blank());
            blocks.Add(H("TOP MEMORY CONSUMERS"));

            int limit = Math.Min(memory.TopTypesBySize.Count, TopTypesToShow);
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                TypeSnapshot type = memory.TopTypesBySize[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(type.TypeName, 60)),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : (type.Count > 0 ? FormatBytes(type.TotalBytes / (ulong)type.Count) : "—"))));
            }

            blocks.Add(new TableBlock(
                Caption: "Top object types by shallow size",
                Headers: ["Type", "Count", "Shallow Size", "Avg Size"],
                Rows: rows));

            if (memory.TopTypesBySize.Count > TopTypesToShow)
                blocks.Add(T($"Showing top {TopTypesToShow:N0} shallow-size types. {memory.TopTypesBySize.Count - TopTypesToShow:N0} additional type(s) omitted."));
        }

        if (gcGen is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("GENERATION PRESSURE"));
            blocks.Add(M("Gen0 objects", gcGen.Gen0Objects.ToString("N0"), gcGen.Gen0Objects));
            blocks.Add(M("Gen1 objects", gcGen.Gen1Objects.ToString("N0"), gcGen.Gen1Objects));
            blocks.Add(M("Gen2 objects", gcGen.Gen2Objects.ToString("N0"), gcGen.Gen2Objects));
            blocks.Add(M("LOH objects", gcGen.LohObjects.ToString("N0"), gcGen.LohObjects));
            blocks.Add(M("Gen2 share", $"{gcGen.Gen2Pct:F1}%", gcGen.Gen2Pct));

            if (gcGen.TopLohTypes.Count > 0)
            {
                int limit = Math.Min(gcGen.TopLohTypes.Count, TopLohTypesToShow);
                var rows = new List<TableRow>(limit);
                for (int i = 0; i < limit; i++)
                {
                    TypeSnapshot type = gcGen.TopLohTypes[i];
                    rows.Add(Row(
                        Cell(FormatHelper.TruncateString(type.TypeName, 60)),
                        Cell(type.Count.ToString("N0"), type.Count),
                        Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue))));
                }

                blocks.Add(new TableBlock(
                    Caption: "Top LOH types",
                    Headers: ["Type", "Count", "Total Size"],
                    Rows: rows));
            }

            if (gcGen.PerTypeGenerationProfiles is { Count: > 0 })
            {
                blocks.Add(T("Per-type generation distribution is available in the analyzer section; this summary only highlights heap-wide generation pressure."));
            }
        }

        blocks.Add(Blank());
        blocks.Add(H("ALLOCATOR NOTE"));
        blocks.Add(T("Allocation-site precision is ETW-dependent; this section summarizes pressure from the dump state only."));

        return new AnalyzerDetailSection(
            AnalyzerName: "Memory Topology",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static string DescribeGcPressure(double gen2Pct, double lohPct)
    {
        if (gen2Pct >= 40.0 || lohPct >= 35.0)
            return "Elevated GC pressure.";

        if (gen2Pct >= 25.0 || lohPct >= 20.0)
            return "Moderate GC pressure.";

        return "GC pressure appears within the low-risk band.";
    }

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