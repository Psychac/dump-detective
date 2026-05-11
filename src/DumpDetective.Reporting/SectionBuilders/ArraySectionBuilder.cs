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
    public int SortOrder => 47; // §22 arrays (before §23 async state machines)

    public bool CanHandle(AnalyzerDomainResult result) => result is ArrayDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ArrayDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("ARRAY ANALYSIS SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Array Objects", $"{d.TotalArrayObjects:N0}", d.TotalArrayObjects));
        blocks.Add(M("Total Array Memory", FormatHelper.FormatBytes(d.TotalArrayBytes)));
        blocks.Add(M("LOH Arrays", $"{d.LohArrayCount:N0} ({FormatHelper.FormatBytes(d.LohArrayBytes)})",
                                                 d.LohArrayCount));
        blocks.Add(M("Multi-Dimensional Arrays", $"{d.MultiDimArrayCount:N0}", d.MultiDimArrayCount));
        if (d.ScanLimited)
            blocks.Add(M("Scan Limit Reached", "Yes — sparse sampling cap hit; results may be partial", 1.0));

        // ── Top array types by memory ─────────────────────────────────────────
        if (d.TopArrayTypesBySize.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ARRAY TYPES BY MEMORY"));
            blocks.Add(T("Array types ranked by total heap bytes consumed. " +
                          "Multi-dimensional arrays (rank ≥ 2) are flagged and are generally slower than jagged arrays."));
            int limit = Math.Min(d.TopArrayTypesBySize.Count, TopTypeRows);
            blocks.Add(new TableBlock(
                Caption: "Top array types by total bytes",
                Headers: ["Element Type", "Rank", "Count", "Total Size", "Multi-Dim"],
                Rows: BuildTypeRows(d.TopArrayTypesBySize, limit)));
            if (d.TopArrayTypesBySize.Count > limit)
                blocks.Add(T($"Showing top {limit} array types by memory. {d.TopArrayTypesBySize.Count - limit} additional type(s) omitted."));
        }

        // ── Top large arrays ──────────────────────────────────────────────────
        if (d.TopLargeArrays.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("LARGEST INDIVIDUAL ARRAYS"));
            blocks.Add(T("Individual array instances on the Large Object Heap (≥85 KB). " +
                          "LOH allocations are never compacted and contribute to heap fragmentation."));
            int limit = Math.Min(d.TopLargeArrays.Count, TopLargeRows);
            blocks.Add(new TableBlock(
                Caption: "Largest individual array instances",
                Headers: ["Address", "Element Type", "Length", "Rank", "Size", "Label"],
                Rows: BuildLargeRows(d.TopLargeArrays, limit)));
            if (d.TopLargeArrays.Count > limit)
                blocks.Add(T($"Showing top {limit} large arrays. {d.TopLargeArrays.Count - limit} additional array(s) omitted."));
        }

        // ── Sparse arrays ─────────────────────────────────────────────────────
        if (d.TopSparseArrays.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("SPARSE / WASTEFUL ARRAYS"));
            blocks.Add(T("Arrays where the majority of elements are null or default. " +
                          "These waste heap memory and could be replaced with sparse data structures such as Dictionary<int, T>."));
            int limit = Math.Min(d.TopSparseArrays.Count, TopSparseRows);
            blocks.Add(new TableBlock(
                Caption: "Sparse arrays by estimated wasted bytes",
                Headers: ["Address", "Element Type", "Length", "Null/Default %", "Wasted Bytes"],
                Rows: BuildSparseRows(d.TopSparseArrays, limit)));
            if (d.TopSparseArrays.Count > limit)
                blocks.Add(T($"Showing top {limit} sparse arrays. {d.TopSparseArrays.Count - limit} additional array(s) omitted."));
        }

        return new AnalyzerDetailSection(AnalyzerName, "Array Analysis", SortOrder, blocks);
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
