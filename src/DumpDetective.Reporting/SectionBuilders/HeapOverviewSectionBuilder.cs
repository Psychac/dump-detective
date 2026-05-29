using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Text.Json;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>
/// Cross-analyzer heap summary chapter that provides a single at-a-glance view
/// over heap size, generations, segment usage, and fragmentation pressure.
/// </summary>
internal sealed class HeapOverviewSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers =>
    [
        "MemoryAnalyzer",
        "GCGenerationAnalyzer",
        "SegmentAnalyzer",
        "LohFragmentationAnalyzer",
        "FinalizableObjectAnalyzer",
        "AllocationPatternAnalyzer"
    ];

    public string SectionId => "prof.heap-overview";
    public string DisplayTitle => "Heap Overview";
    public int SortOrder => 150;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<MemoryDomainResult>() is not null
        || results.Get<GCGenerationDomainResult>() is not null
        || results.Get<SegmentAnalysisDomainResult>() is not null
        || results.Get<LohFragmentationDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        MemoryDomainResult? memory = results.Get<MemoryDomainResult>();
        GCGenerationDomainResult? gc = results.Get<GCGenerationDomainResult>();
        SegmentAnalysisDomainResult? segments = results.Get<SegmentAnalysisDomainResult>();
        LohFragmentationDomainResult? loh = results.Get<LohFragmentationDomainResult>();
        FinalizableObjectDomainResult? finalizable = results.Get<FinalizableObjectDomainResult>();
        AllocationPatternDomainResult? alloc = results.Get<AllocationPatternDomainResult>();

        ulong totalManagedBytes = memory?.TotalBytes
            ?? (gc is not null ? gc.Gen0Bytes + gc.Gen1Bytes + gc.Gen2Bytes + gc.LohBytes : 0);

        double committedUtilization = 0.0;
        double heapFragmentation = 0.0;
        if (segments is not null && segments.TotalCommittedBytes > 0)
        {
            committedUtilization = segments.TotalUsedBytes * 100.0 / segments.TotalCommittedBytes;
            heapFragmentation = 100.0 - committedUtilization;
        }

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Managed Heap", totalManagedBytes > 0 ? FormatBytes(totalManagedBytes) : "N/A", totalManagedBytes > 0 ? (double)totalManagedBytes : null),
            KM("Total Objects", memory?.TotalObjects.ToString("N0") ?? gc?.TotalObjects.ToString("N0") ?? "N/A", memory?.TotalObjects ?? gc?.TotalObjects),
            KM("Gen2 %", gc is null ? "N/A" : $"{gc.Gen2Pct:F1}%", gc?.Gen2Pct),
            KM("LOH %", memory is not null ? $"{memory.LohPercent:F1}%" : gc is not null ? $"{gc.LohPercent:F1}%" : "N/A", memory?.LohPercent ?? gc?.LohPercent),
            KM("Committed Utilization", segments is null ? "N/A" : $"{committedUtilization:F1}%", segments is null ? null : committedUtilization),
            KM("Heap Fragmentation", segments is null ? "N/A" : $"{heapFragmentation:F1}%", segments is null ? null : heapFragmentation),
            KM("LOH Free", loh is null ? "N/A" : $"{loh.FragmentationPercent:F1}%", loh?.FragmentationPercent),
            KM("Finalizer Queue", finalizable?.FinalizerQueueCount.ToString("N0") ?? "N/A", finalizable?.FinalizerQueueCount),
            KM("GC Pressure", alloc?.GCPressure.ToString() ?? "N/A")
        };

        var blocks = new List<SectionBlock>
        {
            T("This chapter consolidates heap shape and GC pressure signals from multiple analyzers into one operational snapshot.")
        };

        if (segments is not null && heapFragmentation >= 30)
        {
            blocks.Add(T($"Heap fragmentation is elevated at {heapFragmentation:F1}% (committed minus used), which can increase allocation and compaction overhead."));
        }

        if (gc is not null && gc.Gen2Pct >= 50)
        {
            blocks.Add(T($"Gen2 occupies {gc.Gen2Pct:F1}% of the heap, indicating long-lived retention pressure."));
        }

        if (loh is not null && loh.FragmentationPercent >= 35)
        {
            blocks.Add(T($"LOH fragmentation is {loh.FragmentationPercent:F1}% with {loh.FreeBlockCount:N0} free blocks; large arrays/strings may be difficult to place contiguously."));
        }

        var tables = new List<SectionTable>();

        if (gc is not null)
        {
            blocks.Add(Chart(
                "Generation memory mix",
                "pie",
                JsonSerializer.Serialize(new
                {
                    title = "Generation memory mix",
                    items = new object[]
                    {
                        new { label = "Gen0", value = gc.Gen0Bytes },
                        new { label = "Gen1", value = gc.Gen1Bytes },
                        new { label = "Gen2", value = gc.Gen2Bytes },
                        new { label = "LOH", value = gc.LohBytes }
                    }
                })));

            ulong totalGenBytes = gc.Gen0Bytes + gc.Gen1Bytes + gc.Gen2Bytes + gc.LohBytes;
            var rows = new List<TableRow>
            {
                Row(Cell("Gen0"), Cell(FormatBytes(gc.Gen0Bytes)), Cell(gc.Gen0Objects.ToString("N0")), Cell(FormatRatio(gc.Gen0Bytes, totalGenBytes))),
                Row(Cell("Gen1"), Cell(FormatBytes(gc.Gen1Bytes)), Cell(gc.Gen1Objects.ToString("N0")), Cell(FormatRatio(gc.Gen1Bytes, totalGenBytes))),
                Row(Cell("Gen2"), Cell(FormatBytes(gc.Gen2Bytes)), Cell(gc.Gen2Objects.ToString("N0")), Cell(FormatRatio(gc.Gen2Bytes, totalGenBytes))),
                Row(Cell("LOH"),  Cell(FormatBytes(gc.LohBytes)),  Cell(gc.LohObjects.ToString("N0")), Cell(FormatRatio(gc.LohBytes, totalGenBytes)))
            };
            tables.Add(ST("Generation distribution", ["Generation", "Bytes", "Objects", "% of Managed"], rows));
        }

        if (segments is not null)
        {
            blocks.Add(Chart(
                "Segment composition",
                "treemap",
                JsonSerializer.Serialize(new
                {
                    title = "Segment composition",
                    items = new object[]
                    {
                        new { label = "SOH", value = segments.SohBytes },
                        new { label = "LOH", value = segments.LohBytes },
                        new { label = "POH", value = segments.PohBytes },
                        new { label = "Frozen", value = segments.FrozenBytes }
                    }
                })));

            ulong committed = segments.TotalCommittedBytes;
            var rows = new List<TableRow>
            {
                Row(Cell("SOH"), Cell(FormatBytes(segments.SohBytes)), Cell(FormatRatio(segments.SohBytes, committed))),
                Row(Cell("LOH"), Cell(FormatBytes(segments.LohBytes)), Cell(FormatRatio(segments.LohBytes, committed))),
                Row(Cell("POH"), Cell(FormatBytes(segments.PohBytes)), Cell(FormatRatio(segments.PohBytes, committed))),
                Row(Cell("Frozen"), Cell(FormatBytes(segments.FrozenBytes)), Cell(FormatRatio(segments.FrozenBytes, committed)))
            };
            tables.Add(ST("Heap segment composition", ["Region", "Committed Bytes", "% of Committed"], rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: DisplayTitle,
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
