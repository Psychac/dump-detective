using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class HeapSegmentDiagnosticsSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.heap-segment-diagnostics";
    public string DisplayTitle => "Heap Segment Diagnostics";
    public int SortOrder => 1450;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<SegmentAnalysisDomainResult>() is not null
        || results.Get<LohFragmentationDomainResult>() is not null
        || results.Get<ArrayDomainResult>() is not null
        || results.Get<MemoryDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        SegmentAnalysisDomainResult? segments = results.Get<SegmentAnalysisDomainResult>();
        LohFragmentationDomainResult? loh = results.Get<LohFragmentationDomainResult>();
        ArrayDomainResult? arrays = results.Get<ArrayDomainResult>();
        MemoryDomainResult? memory = results.Get<MemoryDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("LOH SUMMARY"),
            T("Large object heap, pinned object heap, and frozen heap signals are summarized here."),
        };

        if (segments is not null)
        {
            blocks.Add(M("LOH bytes", FormatBytes(segments.LohBytes), (double)segments.LohBytes));
            blocks.Add(M("LOH segments", segments.LohSegmentCount.ToString("N0"), segments.LohSegmentCount));
            blocks.Add(M("POH bytes", FormatBytes(segments.PohBytes), (double)segments.PohBytes));
            blocks.Add(M("FOH bytes", FormatBytes(segments.FrozenBytes), (double)segments.FrozenBytes));

            if (segments.FrozenBytes > 100UL * 1024 * 1024)
            {
                blocks.Add(T("Frozen object heap usage is above 100 MB; this often points to heavy immutable or interned data retention."));
            }

            if (segments.PerLogicalHeapSummaries.Count > 0)
            {
                blocks.Add(Blank());
                blocks.Add(H("CROSS-HEAP DISTRIBUTION"));
                blocks.Add(T("Per-logical-heap committed bytes, object counts, and segment counts. This is a direct view of heap balance across subheaps."));

                var heapRows = new List<TableRow>(segments.PerLogicalHeapSummaries.Count);
                for (int i = 0; i < segments.PerLogicalHeapSummaries.Count; i++)
                {
                    PerLogicalHeapSummary heap = segments.PerLogicalHeapSummaries[i];
                    double share = segments.TotalCommittedBytes == 0 ? 0.0 : heap.Bytes * 100.0 / segments.TotalCommittedBytes;

                    heapRows.Add(Row(
                        Cell(heap.LogicalHeapIndex.ToString("N0"), heap.LogicalHeapIndex),
                        Cell(FormatBytes(heap.Bytes), (long)Math.Min(heap.Bytes, long.MaxValue)),
                        Cell(share.ToString("F1") + "%"),
                        Cell(heap.ObjectCount >= 0 ? heap.ObjectCount.ToString("N0") : "N/A", heap.ObjectCount),
                        Cell(heap.SegmentCount.ToString("N0"), heap.SegmentCount)));
                }

                blocks.Add(new TableBlock("Per logical heap distribution", ["Heap", "Committed Bytes", "% of Total", "Objects", "Segments"], heapRows));
            }
        }

        if (loh is not null)
        {
            blocks.Add(Blank());
            if (memory?.TopTypesBySize is { Count: > 0 })
            {
                var nearLohRows = new List<TableRow>();
                for (int i = 0; i < memory.TopTypesBySize.Count; i++)
                {
                    TypeSnapshot type = memory.TopTypesBySize[i];
                    if (type.AverageSize <= 85_000 || type.AverageSize >= 200_000)
                        continue;

                    nearLohRows.Add(Row(
                        Cell(type.TypeName),
                        Cell(type.Count.ToString("N0"), type.Count),
                        Cell(FormatBytes(type.AverageSize), (long)Math.Min(type.AverageSize, long.MaxValue)),
                        Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue))));

                    if (nearLohRows.Count >= 10)
                        break;
                }

                if (nearLohRows.Count > 0)
                {
                    blocks.Add(Blank());
                    blocks.Add(H("TYPES JUST OVER THE LOH THRESHOLD"));
                    blocks.Add(new TableBlock("Approximate near-LOH types", ["Type", "Count", "Avg Size", "Total Size"], nearLohRows));
                }
            }
            blocks.Add(H("FRAGMENTATION METRICS"));
            blocks.Add(M("Fragmentation percent", $"{loh.FragmentationPercent:F1}%", loh.FragmentationPercent));
            blocks.Add(M("Free blocks", loh.FreeBlockCount.ToString("N0"), loh.FreeBlockCount));
            blocks.Add(M("Largest free block", FormatBytes(loh.LargestFreeBlock), (double)loh.LargestFreeBlock));
            blocks.Add(T(loh.FragmentationPercent > 60.0
                ? "Fragmentation is critical; compaction or large-object pooling is recommended."
                : loh.FragmentationPercent > 30.0
                    ? "Fragmentation is elevated; monitor large-object churn."
                    : "LOH fragmentation is within a lower-risk band."));

            if (loh.TopFragmentedSegments is { Count: > 0 })
            {
                var rows = new List<TableRow>(loh.TopFragmentedSegments.Count);
                for (int i = 0; i < loh.TopFragmentedSegments.Count; i++)
                {
                    LohSegmentSnapshot snapshot = loh.TopFragmentedSegments[i];
                    rows.Add(Row(
                        Cell($"0x{snapshot.Address:X}"),
                        Cell(FormatBytes(snapshot.FreeBytes), (long)Math.Min(snapshot.FreeBytes, long.MaxValue)),
                        Cell(snapshot.FragmentationPercent.ToString("F1") + "%"),
                        Cell(FormatBytes(snapshot.LargestFreeBlock), (long)Math.Min(snapshot.LargestFreeBlock, long.MaxValue))));
                }

                blocks.Add(Blank());
                blocks.Add(H("TOP FRAGMENTED SEGMENTS"));
                blocks.Add(new TableBlock("Top fragmented segments", ["Address", "Free Bytes", "Frag %", "Largest Free Block"], rows));
            }

            if (loh.FreeGapHistogram is { Count: > 0 })
            {
                var rows = new List<TableRow>(loh.FreeGapHistogram.Count);
                int total = 0;
                for (int i = 0; i < loh.FreeGapHistogram.Count; i++)
                    total += loh.FreeGapHistogram[i].GapCount;

                for (int i = 0; i < loh.FreeGapHistogram.Count; i++)
                {
                    FreeGapBucket bucket = loh.FreeGapHistogram[i];
                    rows.Add(Row(
                        Cell(bucket.GapSizeRange),
                        Cell(bucket.GapCount.ToString("N0"), bucket.GapCount),
                        Cell(total == 0 ? "0.0%" : (bucket.GapCount * 100.0 / total).ToString("F1") + "%")));
                }

                blocks.Add(Blank());
                blocks.Add(H("FREE GAP DISTRIBUTION"));
                blocks.Add(new TableBlock("Free-gap histogram", ["Gap Size Range", "Count", "% of Gaps"], rows));
            }

            if (loh.TopLargeObjects is { Count: > 0 })
            {
                var rows = new List<TableRow>(loh.TopLargeObjects.Count);
                for (int i = 0; i < loh.TopLargeObjects.Count; i++)
                {
                    LargeObjectSnapshot snapshot = loh.TopLargeObjects[i];
                    rows.Add(Row(
                        Cell(snapshot.TypeName),
                        Cell(FormatBytes(snapshot.Size), (long)Math.Min(snapshot.Size, long.MaxValue)),
                        Cell($"0x{snapshot.Address:X}")));
                }

                blocks.Add(Blank());
                blocks.Add(H("TOP LARGE OBJECTS"));
                blocks.Add(new TableBlock("Top large objects", ["Type", "Size", "Address"], rows));
            }
        }

        if (arrays is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("LARGE OBJECT LIFETIMES"));
            blocks.Add(M("Array objects", arrays.TotalArrayObjects.ToString("N0"), arrays.TotalArrayObjects));
            blocks.Add(M("LOH arrays", arrays.LohArrayCount.ToString("N0"), arrays.LohArrayCount));
            blocks.Add(M("LOH array bytes", FormatBytes(arrays.LohArrayBytes), (double)arrays.LohArrayBytes));
            blocks.Add(T(arrays.ScanLimited
                ? "Array scan was limited; only the sampled large arrays are shown."
                : "Array scan completed within the configured cap."));

            if (arrays.TopLargeArrays.Count > 0)
            {
                var rows = new List<TableRow>(arrays.TopLargeArrays.Count);
                for (int i = 0; i < arrays.TopLargeArrays.Count; i++)
                {
                    LargeArrayEntry entry = arrays.TopLargeArrays[i];
                    rows.Add(Row(
                        Cell($"0x{entry.Address:X}"),
                        Cell(entry.ElementTypeName),
                        Cell(entry.Length.ToString("N0"), entry.Length),
                        Cell(entry.Rank.ToString("N0"), entry.Rank),
                        Cell(FormatBytes(entry.Size), (long)Math.Min(entry.Size, long.MaxValue))));
                }

                blocks.Add(new TableBlock("Large arrays", ["Address", "Element Type", "Length", "Rank", "Size"], rows));
            }
        }

        blocks.Add(Blank());
        blocks.Add(H("POH / FOH NOTES"));
        if (segments?.TopPohTypes is { Count: > 0 })
        {
            var pohRows = new List<TableRow>(segments.TopPohTypes.Count);
            for (int i = 0; i < segments.TopPohTypes.Count; i++)
            {
                TypeSnapshot type = segments.TopPohTypes[i];
                pohRows.Add(Row(
                    Cell(type.TypeName),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : "—")));
            }

            blocks.Add(H("TOP POH TYPES"));
            blocks.Add(new TableBlock("Pinned object heap types", ["Type", "Count", "Size", "Avg Size"], pohRows));
        }
        else
        {
            blocks.Add(T("POH type distribution is not currently available for this dump; use the segment summaries above for pressure analysis."));
        }

        if (segments?.TopFrozenTypes is { Count: > 0 })
        {
            var frozenRows = new List<TableRow>(segments.TopFrozenTypes.Count);
            for (int i = 0; i < segments.TopFrozenTypes.Count; i++)
            {
                TypeSnapshot type = segments.TopFrozenTypes[i];
                frozenRows.Add(Row(
                    Cell(type.TypeName),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : "—")));
            }

            blocks.Add(Blank());
            blocks.Add(H("TOP FOH TYPES"));
            blocks.Add(new TableBlock("Frozen object heap types", ["Type", "Count", "Size", "Avg Size"], frozenRows));
        }
        else
        {
            blocks.Add(T("FOH type distribution is not currently available for this dump; use the segment summaries above for pressure analysis."));
        }

        return new AnalyzerDetailSection("Heap Segment Diagnostics", DisplayTitle, SortOrder, blocks);
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