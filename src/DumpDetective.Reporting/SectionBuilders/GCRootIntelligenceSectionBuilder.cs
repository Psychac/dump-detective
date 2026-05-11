using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCRootIntelligenceSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.gc-root-intelligence";
    public string DisplayTitle => "GC Root Intelligence";
    public int SortOrder => 1200;

    public bool CanBuild(AnalyzerResultSet results) => results.Get<GCRootDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        GCRootDomainResult? roots = results.Get<GCRootDomainResult>();
        var blocks = new List<SectionBlock>
        {
            H("ROOT DISTRIBUTION"),
            T("Average retained bytes are heuristic estimates unless a targeted retained-size pass is available."),
        };

        if (roots is null)
        {
            blocks.Add(T("No GC root result was available."));
            return new AnalyzerDetailSection("GC Root Intelligence", DisplayTitle, SortOrder, blocks);
        }

        blocks.Add(new TableBlock(
            Caption: "GC root kinds",
            Headers: ["Root Kind", "Count", "Estimated Retained", "% of Heap"],
            Rows: BuildKindRows(roots.ByKind)));

        blocks.Add(Blank());
        blocks.Add(H("ROOT SEVERITY RANKING"));
        blocks.Add(new TableBlock(
            Caption: "Top GC roots by severity",
            Headers: ["Root Kind", "Target Type", "Field", "Est. Retained", "Severity", "Root Addr"],
            Rows: BuildSeverityRows(roots.TopRootsBySeverity)));

        var finalizerRoots = roots.TopRootsBySeverity.Where(root => string.Equals(root.RootKind, "FinalizerQueue", StringComparison.Ordinal)).ToList();
        if (finalizerRoots.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("FINALIZER ROOTS"));
            blocks.Add(new TableBlock(
                Caption: "Finalizer roots",
                Headers: ["Target Type", "Field", "Est. Retained", "Severity", "Root Addr"],
                Rows: finalizerRoots.Take(10).Select(root => Row(
                    Cell(root.TargetTypeName),
                    Cell(root.FieldDescription ?? "—"),
                    Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                    Cell($"0x{root.RootAddress:X}"))).ToList()));
        }

        blocks.Add(Blank());
        blocks.Add(H("ROOT PATHS BY TARGET TYPE"));
        blocks.Add(T("Root paths are grouped by target type and shown shortest-first within each group."));

        foreach (var group in roots.RootPaths
            .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key))
        {
            int limit = Math.Min(group.Count(), 3);
            blocks.Add(CollapseBegin($"{group.Key} ({group.Count()} path(s))"));

            var rows = new List<TableRow>(limit);
            foreach (var path in group.OrderBy(p => p.PathLength).Take(limit))
            {
                rows.Add(Row(
                    Cell(path.RootKind),
                    Cell(path.PathLength.ToString("N0"), path.PathLength),
                    Cell(path.WasCapped ? "yes" : "no"),
                    Cell(FormatPath(path))));
            }

            blocks.Add(new TableBlock(
                Caption: null,
                Headers: ["Root Kind", "Path Length", "Capped", "Path"],
                Rows: rows));
            blocks.Add(CollapseEnd());
            blocks.Add(Blank());
        }

        return new AnalyzerDetailSection("GC Root Intelligence", DisplayTitle, SortOrder, blocks);
    }

    private static List<TableRow> BuildKindRows(IReadOnlyList<RootKindSummary> kinds)
    {
        var rows = new List<TableRow>(kinds.Count);
        for (int i = 0; i < kinds.Count; i++)
        {
            RootKindSummary kind = kinds[i];
            rows.Add(Row(
                Cell(kind.Kind),
                Cell(kind.Count.ToString("N0"), kind.Count),
                Cell(FormatBytes(kind.EstimatedRetainedBytes), (long)Math.Min(kind.EstimatedRetainedBytes, long.MaxValue)),
                Cell(kind.PctOfManagedHeap.ToString("F1") + "%")));
        }

        return rows;
    }

    private static List<TableRow> BuildSeverityRows(IReadOnlyList<RootFinding> roots)
    {
        var rows = new List<TableRow>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            RootFinding root = roots[i];
            rows.Add(Row(
                Cell(root.RootKind),
                Cell(root.TargetTypeName),
                Cell(root.FieldDescription ?? "—"),
                Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                Cell($"0x{root.RootAddress:X}")));
        }

        return rows;
    }

    private static string FormatPath(RootPathFinding path)
    {
        var builder = new System.Text.StringBuilder();
        bool isIndirect = false;
        builder.Append('[').Append(path.RootKind).Append("] ");

        for (int i = 0; i < path.PathTypeNames.Count; i++)
        {
            if (i > 0)
                builder.Append(" → ");

            string typeName = path.PathTypeNames[i];
            if (typeName.Contains("object[]", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("List`1", StringComparison.OrdinalIgnoreCase))
            {
                isIndirect = true;
            }

            builder.Append(TrimTypeName(typeName));
        }

        if (path.WasCapped)
            builder.Append(" [TRUNCATED]");

        if (isIndirect)
            builder.Append(" (indirect)");

        return builder.ToString();
    }

    private static string TrimTypeName(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
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