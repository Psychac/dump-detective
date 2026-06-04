using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ArraySectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;
    private const int TopLargeRows = 20;
    private const int TopSparseRows = 10;

    public string AnalyzerName => "Array Analysis";
    public string DisplayTitle => "Arrays";
    public int SortOrder => 400; // §22 arrays (before §23 async state machines)

    public bool CanHandle(AnalyzerDomainResult result) => result is ArrayDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ArrayDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_array_objects"] = new NumericMetricValue(d.TotalArrayObjects, MetricUnit.Count),
            ["total_array_memory"] = new NumericMetricValue((double)d.TotalArrayBytes, MetricUnit.Bytes),
            ["loh_arrays"] = new NumericMetricValue(d.LohArrayCount, MetricUnit.Count),
            ["loh_array_bytes"] = new NumericMetricValue((double)d.LohArrayBytes, MetricUnit.Bytes),
            ["multi_dimensional_arrays"] = new NumericMetricValue(d.MultiDimArrayCount, MetricUnit.Count),
        };
        if (d.ScanLimited)
            keyMetrics["scan_limit_reached"] = new TextMetricValue("Yes — sparse sampling cap hit; results may be partial");

        if (d.TopArrayTypesBySize.Count > 0)
        {
            int limit = Math.Min(d.TopArrayTypesBySize.Count, TopTypeRows);
            tables.Add(ST(
                "Top array types by total bytes",
                ["Element Type", "Rank", "Count", "Total Size", "Multi-Dim"],
                BuildTypeRows(d.TopArrayTypesBySize, limit)));
            if (d.TopArrayTypesBySize.Count > limit)
                blocks.Add(T($"Showing top {limit} array types by memory. {d.TopArrayTypesBySize.Count - limit} additional type(s) omitted."));
        }

        if (d.TopLargeArrays.Count > 0)
        {
            blocks.Add(T("Individual array instances on the Large Object Heap (≥85 KB). " +
                          "LOH allocations are never compacted and contribute to heap fragmentation."));
            int limit = Math.Min(d.TopLargeArrays.Count, TopLargeRows);
            tables.Add(ST(
                "Largest individual array instances",
                ["Address", "Element Type", "Length", "Rank", "Size", "Label"],
                BuildLargeRows(d.TopLargeArrays, limit)));
            if (d.TopLargeArrays.Count > limit)
                blocks.Add(T($"Showing top {limit} large arrays. {d.TopLargeArrays.Count - limit} additional array(s) omitted."));
        }

        if (d.TopSparseArrays.Count > 0)
        {
            blocks.Add(T("Arrays where the majority of elements are null or default. " +
                          "These waste heap memory and could be replaced with sparse data structures such as Dictionary<int, T>."));
            int limit = Math.Min(d.TopSparseArrays.Count, TopSparseRows);
            tables.Add(ST(
                "Sparse arrays by estimated wasted bytes",
                ["Address", "Element Type", "Length", "Null/Default %", "Wasted Bytes"],
                BuildSparseRows(d.TopSparseArrays, limit)));
            if (d.TopSparseArrays.Count > limit)
                blocks.Add(T($"Showing top {limit} sparse arrays. {d.TopSparseArrays.Count - limit} additional array(s) omitted."));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<ArrayTypeProfile> types, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            ArrayTypeProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.ElementTypeName, 70)),
                Cell($"{t.Rank:N0}",                        t.Rank),
                Cell($"{t.Count:N0}",                       t.Count),
                Cell(FormatHelper.FormatBytes(t.TotalBytes)),
                Cell(t.IsMultiDimensional ? "Yes" : "No"),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildLargeRows(IReadOnlyList<LargeArrayEntry> entries, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            LargeArrayEntry e = entries[i];
            rows.Add(new TableRow([
                Cell($"0x{e.Address:X}"),
                Cell(FormatHelper.TruncateString(e.ElementTypeName, 70)),
                Cell($"{e.Length:N0}",                      e.Length),
                Cell($"{e.Rank:N0}",                        e.Rank),
                Cell(FormatHelper.FormatBytes(e.Size)),
                Cell(GetAntiPatternLabel(e.ElementTypeName, e.Length, e.Size)),
            ]));
        }
        return rows;
    }

    private static string GetAntiPatternLabel(string elementTypeName, int length, ulong size)
    {
        if (elementTypeName.Contains("Byte", StringComparison.OrdinalIgnoreCase) && size > 1_000_000)
            return "byte[] > 1 MB";

        if ((elementTypeName.Contains("String", StringComparison.OrdinalIgnoreCase) || elementTypeName.Contains("Object", StringComparison.OrdinalIgnoreCase))
            && length > 10_000)
            return $"{elementTypeName}[] > 10k";

        return "—";
    }

    private static List<TableRow> BuildSparseRows(IReadOnlyList<SparseArrayEntry> entries, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            SparseArrayEntry e = entries[i];
            rows.Add(new TableRow([
                Cell($"0x{e.Address:X}"),
                Cell(FormatHelper.TruncateString(e.ElementTypeName, 70)),
                Cell($"{e.Length:N0}",                      e.Length),
                Cell($"{e.SparseRatio:P0}"),
                Cell(FormatHelper.FormatBytes(e.WastedBytes)),
            ]));
        }
        return rows;
    }
}
