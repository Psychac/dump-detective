using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCRootIntelligenceSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "GC Root Analysis";
    public string DisplayTitle => "GC Root Intelligence";
    public int SortOrder => 500;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var roots = (GCRootDomainResult)result;

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.55, ["Average retained bytes are heuristic estimates."]),
            T("Average retained bytes are heuristic estimates unless a targeted retained-size pass is available."),
        };

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Roots",           roots.TotalRoots.ToString("N0"),           roots.TotalRoots),
            KM("Path Search Capped",    roots.PathSearchCapped ? $"Yes ({roots.PathSearchCappedCount:N0} capped)" : "No"),
        };

        tables.Add(ST(
            "GC root kinds",
            ["Root Kind", "Count", "Estimated Retained", "% of Heap"],
            BuildKindRows(roots.ByKind)));

        tables.Add(ST(
            "Top GC roots by severity",
            ["Root Kind", "Root Addr", "Field", "Target Type", "Target Addr", "Est. Retained", "Severity"],
            BuildSeverityRows(roots.TopRootsBySeverity)));

        var finalizerRoots = roots.TopRootsBySeverity.Where(root => string.Equals(root.RootKind, "FinalizerQueue", StringComparison.Ordinal)).ToList();
        if (finalizerRoots.Count > 0)
        {
            tables.Add(ST(
                "Finalizer roots",
                ["Target Type", "Field", "Est. Retained", "Severity", "Root Addr"],
                finalizerRoots.Take(10).Select(root => Row(
                    Cell(root.TargetTypeName),
                    Cell(root.FieldDescription ?? "—"),
                    Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                    Cell($"0x{root.RootAddress:X}"))).ToList()));
        }

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

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
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
                Cell($"0x{root.RootAddress:X}"),
                Cell(root.FieldDescription ?? "—"),
                Cell(root.TargetTypeName),
                Cell($"0x{root.TargetAddress:X}"),
                Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                Cell(root.SeverityScore.ToString("N0"), root.SeverityScore)));
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