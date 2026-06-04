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

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_roots"] = new NumericMetricValue(roots.TotalRoots, MetricUnit.Count),
            ["path_search_capped"] = new TextMetricValue(roots.PathSearchCapped ? $"Yes ({roots.PathSearchCappedCount:N0} capped)" : "No"),
        };

        tables.Add(ST(
            "GC root kinds",
            ["Root Kind", "Count", "Estimated Retained", "% of Heap"],
            BuildKindRows(roots.ByKind)));

        tables.Add(ST(
            "Top GC roots by severity",
            ["Root Kind", "Root Addr", "Field", "Target Type", "Target Addr", "Est. Retained", "Severity"],
            BuildSeverityRows(roots.TopRootsBySeverity)));

        var finalizerRoots = roots.TopRootsBySeverity.Where(root => string.Equals(root.RootKind, "FinalizerQueue", StringComparison.Ordinal)).ToArray();
        if (finalizerRoots.Length > 0)
        {
            tables.Add(ST(
                "Finalizer roots",
                ["Target Type", "Field", "Est. Retained", "Severity", "Root Addr"],
                finalizerRoots.Take(10).Select(root => Row(
                    Cell(root.TargetTypeName),
                    Cell(root.FieldDescription ?? "—"),
                    Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                    Cell($"0x{root.RootAddress:X}"))).ToArray()));
        }

        // ── Root paths: outer collapsible wrapper ─────────────────────────
        const int ChainInitial = 5;

        var pathGroups = roots.RootPaths
            .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToArray();

        string outerTitle = roots.PathSearchCapped
            ? $"Root paths by target type ({pathGroups.Length} type(s)) ⚠ some paths truncated"
            : $"Root paths by target type ({pathGroups.Length} type(s))";

        blocks.Add(CollapseBegin(outerTitle));
        blocks.Add(T($"Grouped by target type, shortest path first. Reference chains longer than {ChainInitial} hops are collapsed — expand inline to see the full chain."));

        foreach (var group in pathGroups)
        {
            var pathsInGroup = group.OrderBy(p => p.PathLength).Take(3).ToArray();
            bool anyGroupCapped = pathsInGroup.Any(p => p.WasCapped);
            string shortName = TrimTypeName(group.Key);
            string groupTitle = anyGroupCapped
                ? $"{shortName} ({group.Count()} path(s)) ⚠ truncated"
                : $"{shortName} ({group.Count()} path(s))";

            blocks.Add(CollapseBegin(groupTitle));
            blocks.Add(T(group.Key)); // full qualified name

            for (int pi = 0; pi < pathsInGroup.Length; pi++)
            {
                var path = pathsInGroup[pi];
                if (pi > 0)
                    blocks.Add(Divider());

                blocks.Add(M("Root Kind",   path.RootKind));
                blocks.Add(M("Target Addr", $"0x{path.TargetAddress:X}"));
                blocks.Add(M("Path Length", path.WasCapped
                    ? $"{path.PathLength}+ (truncated)"
                    : path.PathLength.ToString("N0")));

                if (path.PathTypeNames.Count > 0)
                {
                    blocks.Add(H("Reference chain:"));
                    blocks.Add(Li($"[{path.RootKind}] (root)"));

                    int shown = Math.Min(path.PathTypeNames.Count, ChainInitial);
                    for (int hi = 0; hi < shown; hi++)
                        blocks.Add(Li($"→ {path.PathTypeNames[hi]}"));

                    int remaining = path.PathTypeNames.Count - shown;
                    if (remaining > 0 || path.WasCapped)
                    {
                        string overflowTitle = remaining > 0
                            ? $"… show {remaining} more hop(s){(path.WasCapped ? " (truncated)" : string.Empty)}"
                            : "… (truncated — further references may exist)";
                        blocks.Add(CollapseBegin(overflowTitle));
                        for (int hi = shown; hi < path.PathTypeNames.Count; hi++)
                            blocks.Add(Li($"→ {path.PathTypeNames[hi]}"));
                        if (path.WasCapped)
                            blocks.Add(Li("→ … (truncated — further references may exist)"));
                        blocks.Add(CollapseEnd());
                    }
                }
                else
                {
                    blocks.Add(T("No intermediate references recorded."));
                }
            }

            blocks.Add(CollapseEnd()); // end type group
            blocks.Add(Blank());
        }

        blocks.Add(CollapseEnd()); // end outer root-paths section

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