using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCRootSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopKindRows = 10;
    private const int TopFindingRows = 20;
    private const int TopPathRows = 10;
    private const int PathTypesCap = 8; // max type names to show per path

    public string AnalyzerName => "GC Root Analysis";
    public int SortOrder => 24; // §5 root intelligence — between GCHandle (§9.3) and memory leak (§6)

    public bool CanHandle(AnalyzerDomainResult result) => result is GCRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (GCRootDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ───────────────────────────────────────────────────────────
        blocks.Add(H("GC ROOT INTELLIGENCE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total GC Roots", $"{d.TotalRoots:N0}", d.TotalRoots));
        blocks.Add(M("Root Kinds Identified", $"{d.ByKind.Count}", d.ByKind.Count));
        blocks.Add(M("Top Suspects Ranked", $"{d.TopRootsBySeverity.Count}", d.TopRootsBySeverity.Count));
        blocks.Add(M("Path Searches Capped", d.PathSearchCapped ? $"Yes ({d.PathSearchCappedCount})" : "No",
                                                 d.PathSearchCapped ? 1.0 : 0.0));

        // ── Root kind distribution ─────────────────────────────────────────
        if (d.ByKind.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("ROOT KIND DISTRIBUTION"));
            int kindLimit = Math.Min(d.ByKind.Count, TopKindRows);
            blocks.Add(new TableBlock(
                Caption: "GC root kind distribution",
                Headers: ["Root Kind", "Count", "Est. Retained", "% of Heap"],
                Rows: BuildKindRows(d.ByKind, kindLimit)));
        }

        // ── Top roots by severity ─────────────────────────────────────────
        if (d.TopRootsBySeverity.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ROOTS BY SEVERITY (RANKED)"));
            int findingLimit = Math.Min(d.TopRootsBySeverity.Count, TopFindingRows);
            blocks.Add(new TableBlock(
                Caption: "Top GC roots by severity score",
                Headers: ["Root Kind", "Target Type", "Est. Retained", "Severity", "Root Addr"],
                Rows: BuildFindingRows(d.TopRootsBySeverity, findingLimit)));
        }

        // ── Root paths ────────────────────────────────────────────────────
        if (d.RootPaths.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("ROOT RETENTION PATHS (FORWARD BFS)"));
            blocks.Add(T("Each path shows the type chain reachable from the root's direct target object (forward traversal, depth ≤ 20, nodes ≤ 500)."));
            blocks.AddRange(BuildGroupedPathBlocks(d.RootPaths));
        }

        return new AnalyzerDetailSection(AnalyzerName, "GC Root Intelligence", SortOrder, blocks);
    }

    private static List<TableRow> BuildKindRows(IReadOnlyList<RootKindSummary> kinds, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            RootKindSummary k = kinds[i];
            rows.Add(new TableRow([
                Cell(k.Kind),
                Cell($"{k.Count:N0}", k.Count),
                Cell(FormatHelper.FormatBytes(k.EstimatedRetainedBytes)),
                Cell($"{k.PctOfManagedHeap:F1}%"),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildFindingRows(IReadOnlyList<RootFinding> findings, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            RootFinding f = findings[i];
            rows.Add(new TableRow([
                Cell(f.RootKind),
                Cell(FormatHelper.TruncateString(f.TargetTypeName, 65)),
                Cell(FormatHelper.FormatBytes(f.EstimatedRetainedBytes)),
                Cell($"{f.SeverityScore}", f.SeverityScore),
                Cell($"0x{f.RootAddress:X}"),
            ]));
        }
        return rows;
    }

    private static IReadOnlyList<SectionBlock> BuildGroupedPathBlocks(IReadOnlyList<RootPathFinding> paths)
    {
        var grouped = new Dictionary<string, List<RootPathFinding>>(StringComparer.Ordinal);
        var order = new List<string>();

        for (int i = 0; i < paths.Count; i++)
        {
            RootPathFinding p = paths[i];
            if (!grouped.TryGetValue(p.TargetTypeName, out var group))
            {
                group = [];
                grouped[p.TargetTypeName] = group;
                order.Add(p.TargetTypeName);
            }

            group.Add(p);
        }

        order.Sort((a, b) => grouped[b].Count.CompareTo(grouped[a].Count));

        var blocks = new List<SectionBlock>();
        int groupLimit = Math.Min(order.Count, TopPathRows);
        for (int i = 0; i < groupLimit; i++)
        {
            string targetType = order[i];
            List<RootPathFinding> group = grouped[targetType];
            group.Sort((a, b) => a.PathLength.CompareTo(b.PathLength));

            blocks.Add(CollapseBegin($"{targetType} ({group.Count:N0} path(s))"));
            blocks.Add(new TableBlock(
                Caption: $"Paths for {targetType}",
                Headers: ["Root Kind", "Path Length", "Capped", "First Types Seen"],
                Rows: BuildPathRows(group, 3)));
            blocks.Add(CollapseEnd());
        }

        if (order.Count > TopPathRows)
            blocks.Add(T($"Showing top {TopPathRows} target type group(s). {order.Count - TopPathRows} additional target type(s) omitted."));

        return blocks;
    }

    private static List<TableRow> BuildPathRows(IReadOnlyList<RootPathFinding> paths, int limit)
    {
        var rows = new List<TableRow>(Math.Min(paths.Count, limit));
        for (int i = 0; i < paths.Count && i < limit; i++)
        {
            RootPathFinding p = paths[i];

            int showCount = Math.Min(p.PathTypeNames.Count, PathTypesCap);
            var typeList = new System.Text.StringBuilder();
            for (int j = 0; j < showCount; j++)
            {
                if (j > 0) typeList.Append(" → ");
                string typeName = p.PathTypeNames[j];
                // Strip namespace prefix for readability
                int dot = typeName.LastIndexOf('.');
                typeList.Append(dot >= 0 ? typeName.AsSpan(dot + 1).ToString() : typeName);
            }
            if (p.PathTypeNames.Count > PathTypesCap)
                typeList.Append(" …");
            if (p.WasCapped)
                typeList.Append(" [TRUNCATED]");

            rows.Add(new TableRow([
                Cell(p.RootKind),
                Cell($"{p.PathLength}", p.PathLength),
                Cell(p.WasCapped ? "yes" : "no"),
                Cell(typeList.ToString()),
            ]));
        }
        return rows;
    }
}
