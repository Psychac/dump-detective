using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

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

        // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): once the
        // dominator tree resolved every kind's retained-byte total exactly, the long-standing
        // "heuristic estimate" caveat no longer applies to this report.
        bool anyKindIsHeuristic = roots.ByKind.Count == 0 || roots.ByKind.Any(k => !k.IsExactRetainedBytes);

        var (confidenceScore, capCaveats) = ConfidenceScoring.Compute(0.75,
            ConfidenceScoring.F(roots.PathSearchCapped, 0.20, $"Root path search was capped ({roots.PathSearchCappedCount:N0} path(s) truncated)."));

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();
        if (anyKindIsHeuristic)
        {
            blocks.Add(BuildConfidenceBand(confidenceScore,
                new[] { "Average retained bytes are heuristic estimates." }.Concat(capCaveats).ToArray()));
            blocks.Add(T("Average retained bytes are heuristic estimates unless a targeted retained-size pass is available."));
        }
        else
        {
            blocks.Add(BuildConfidenceBand(confidenceScore, capCaveats.ToArray()));
            blocks.Add(T("Retained bytes are exact — computed from the dominator tree, not a heuristic estimate."));
        }
        blocks.Add(T("Root-owned subgraph types show the object types reachable from each root. For exact root-to-target retention chains, use WinDbg !gcroot or dotMemory."));

        if (roots.DroppedZeroEstimateRootCount > 0)
        {
            blocks.Add(T($"{roots.DroppedZeroEstimateRootCount:N0} root(s) were dropped from the analysis " +
                "(null target, unresolvable object metadata, or zero-size object) and contribute no retained-byte estimate."));
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_roots"] = new NumericMetricValue(roots.TotalRoots, MetricUnit.Count),
            ["path_search_capped"] = new TextMetricValue(roots.PathSearchCapped ? $"Yes ({roots.PathSearchCappedCount:N0} capped)" : "No"),
            ["dropped_zero_estimate_roots"] = new NumericMetricValue(roots.DroppedZeroEstimateRootCount, MetricUnit.Count),
        };

        compactTables.Add(STCompact(
            "GC root kinds",
            new[] { CH("Root Kind"), CH("Count","number"), CH("Estimated Retained","bytes"), CH("% of Heap", "number", "percent"), CH("Exact?"), CH("Gen0 %", "number", "percent"), CH("Gen1 %", "number", "percent"), CH("Gen2 %", "number", "percent"), CH("LOH %", "number", "percent") },
            BuildKindRows(roots.ByKind).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        compactTables.Add(STCompact(
            "Top GC roots by severity",
            new[] { CH("Root Kind"), CH("Root Addr"), CH("Field"), CH("Target Type"), CH("Target Addr"), CH("Est. Retained","bytes"), CH("Severity","number"), CH("Exact?") },
            BuildSeverityRows(roots.TopRootsBySeverity).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        var finalizerRoots = roots.TopRootsBySeverity.Where(root => string.Equals(root.RootKind, "FinalizerQueue", StringComparison.Ordinal)).ToArray();
        if (finalizerRoots.Length > 0)
        {
            compactTables.Add(STCompact(
                "Finalizer roots",
                new[] { CH("Target Type"), CH("Field"), CH("Est. Retained","bytes"), CH("Severity","number"), CH("Root Addr") },
                finalizerRoots.Select(root => R(new object?[] { root.TargetTypeName, root.FieldDescription ?? "—", root.EstimatedRetainedBytes, root.SeverityScore, $"0x{root.RootAddress:X}" })).ToArray()));

            // P2-1 (docs/analysis/phase1/gcroot-analyzer-audit.md): per-type breakdown of the same
            // finalizer roots above — complete ranked population, no top-N cap (matches
            // LeakCandidateDomainResult.TopCandidates precedent: the render layer paginates, not
            // the data). Immediately surfaces which type(s) dominate finalization pressure instead
            // of requiring the reader to eyeball the flat per-root table above.
            var finalizerByType = finalizerRoots
                .GroupBy(root => root.TargetTypeName, StringComparer.Ordinal)
                .Select(g => (TypeName: g.Key, Count: g.Count(), TotalRetainedBytes: g.Aggregate(0UL, (sum, root) => sum + root.EstimatedRetainedBytes)))
                .OrderByDescending(t => t.Count)
                .ThenByDescending(t => t.TotalRetainedBytes)
                .ToArray();

            compactTables.Add(STCompact(
                "Finalizer queue by type",
                new[] { CH("Target Type"), CH("Count", "number"), CH("Total Est. Retained", "bytes") },
                finalizerByType.Select(t => R(new object?[] { t.TypeName, t.Count, t.TotalRetainedBytes })).ToArray()));
        }

        // ── Root paths: typed RootPathGroups slot ─────────────────────────
        var rootPathGroups = new List<RootPathGroup>();

        if (roots.RootPaths.Count > 0)
        {
            var pathGroupings = roots.RootPaths
                .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key);

            foreach (var group in pathGroupings)
            {
                var pathsInGroup = group.OrderBy(p => p.PathLength).Take(3).ToArray();
                bool anyGroupCapped = false;
                for (int pi = 0; pi < pathsInGroup.Length; pi++)
                    if (pathsInGroup[pi].WasCapped) { anyGroupCapped = true; break; }

                var typedPaths = new List<RootPath>(pathsInGroup.Length);
                for (int pi = 0; pi < pathsInGroup.Length; pi++)
                {
                    var p = pathsInGroup[pi];
                    typedPaths.Add(new RootPath(
                        RootKind:      p.RootKind,
                        TargetAddress: $"0x{p.TargetAddress:X8}",
                        PathLength:    p.PathLength,
                        WasCapped:     p.WasCapped,
                        Hops:          p.PathTypeNames,
                        EstimatedRetainedBytes:  p.EstimatedRetainedBytes,
                        RetainedSizeWasWalked:   p.RetainedSizeWasWalked,
                        RetainedSizeIsExact:     p.RetainedSizeIsExact));
                }

                rootPathGroups.Add(new RootPathGroup(
                    TargetType:      group.Key,
                    TargetTypeShort: TrimTypeName(group.Key),
                    TotalPathCount:  group.Count(),
                    AnyCapped:       anyGroupCapped,
                    Paths:           typedPaths));
            }

            if (roots.PathSearchCapped)
                blocks.Add(T($"Root path search was capped ({roots.PathSearchCappedCount:N0} path(s) truncated) — some types may have incomplete chains."));
        }

        // P3-4 (docs/analysis/phase1/gcroot-analyzer-audit.md): typed TreeWidgets slot — collapses
        // RootPathGroups chains that share a common structure near the target/root end into one
        // shared-prefix tree per group, instead of one independent chain card per path (rendered by
        // the same shared collapsible tree widget ThreadStackClusterAnalyzer's cluster tree uses —
        // see docs/refactor/collapsible-tree-widget-design.md). RootPathGroups above is unchanged
        // and still emitted for consumers that only want the flat per-path view.
        List<TreeWidget>? treeWidgets = roots.RootPaths.Count > 0 ? BuildRootPathTreeWidgets(roots.RootPaths) : null;

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            RootPathGroups: rootPathGroups.Count > 0 ? rootPathGroups : null,
            TreeWidgets: treeWidgets);
    }

    // ── P3-4: shared-prefix tree over each RootPathGroup's forward-walk hop sequences ──────────
    // Mirrors ThreadStackClusterAnalyzer.BuildClusterTree's trie-merge shape (see that method's
    // docs/refactor/collapsible-tree-widget-design.md reference), scoped to RootPathFinding.PathTypeNames
    // instead of stack-frame signatures. Every path in a group already shares hop[0] by construction
    // (grouped by TargetTypeName), so that hop becomes the group's tree root label and only hops[1..]
    // are merged.
    private const int MaxRootPathTreeNodes = 400;
    private const int MaxRootPathTreeChildren = 8;

    private static List<TreeWidget> BuildRootPathTreeWidgets(IReadOnlyList<RootPathFinding> rootPaths)
    {
        var byTargetType = rootPaths
            .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        int nodeBudget = MaxRootPathTreeNodes;
        var groupRoots = new List<TreeNode>();
        bool anyTruncated = false;

        foreach (var group in byTargetType)
        {
            if (nodeBudget <= 0) { anyTruncated = true; break; }
            nodeBudget--;

            var trieRoot = new RootPathTrieNode();
            int pathCount = 0;
            foreach (RootPathFinding path in group)
            {
                pathCount++;
                RootPathTrieNode node = trieRoot;
                for (int i = 1; i < path.PathTypeNames.Count; i++)
                {
                    node = GetOrAddChild(node, path.PathTypeNames[i]);
                    node.Count++;
                }
            }

            List<KeyValuePair<string, RootPathTrieNode>> orderedChildren = OrderChildrenByCountDescending(trieRoot.Children);
            var children = new List<TreeNode>(Math.Min(orderedChildren.Count, MaxRootPathTreeChildren));
            int childTruncated = 0;
            for (int i = 0; i < orderedChildren.Count; i++)
            {
                if (children.Count < MaxRootPathTreeChildren && nodeBudget > 0)
                    children.Add(ConvertRootPathTrieNode(orderedChildren[i].Key, orderedChildren[i].Value, ref nodeBudget));
                else
                    childTruncated++;
            }
            if (childTruncated > 0)
                anyTruncated = true;

            groupRoots.Add(new TreeNode(TrimTypeName(group.Key), pathCount, "paths",
                children.Count > 0 ? children : null, childTruncated));
        }

        return
        [
            new TreeWidget("Root-owned subgraph shapes (shared structure collapsed)", groupRoots, anyTruncated)
        ];
    }

    private static RootPathTrieNode GetOrAddChild(RootPathTrieNode node, string typeName)
    {
        if (!node.Children.TryGetValue(typeName, out RootPathTrieNode? child))
        {
            child = new RootPathTrieNode();
            node.Children[typeName] = child;
        }
        return child;
    }

    private static List<KeyValuePair<string, RootPathTrieNode>> OrderChildrenByCountDescending(Dictionary<string, RootPathTrieNode> children)
    {
        var ordered = new List<KeyValuePair<string, RootPathTrieNode>>(children);
        ordered.Sort(static (a, b) =>
        {
            int byCount = b.Value.Count.CompareTo(a.Value.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });
        return ordered;
    }

    private static TreeNode ConvertRootPathTrieNode(string typeName, RootPathTrieNode node, ref int nodeBudget)
    {
        nodeBudget--;

        List<KeyValuePair<string, RootPathTrieNode>> orderedChildren = OrderChildrenByCountDescending(node.Children);
        var children = new List<TreeNode>(Math.Min(orderedChildren.Count, MaxRootPathTreeChildren));
        int truncatedChildCount = 0;
        for (int i = 0; i < orderedChildren.Count; i++)
        {
            if (children.Count < MaxRootPathTreeChildren && nodeBudget > 0)
                children.Add(ConvertRootPathTrieNode(orderedChildren[i].Key, orderedChildren[i].Value, ref nodeBudget));
            else
                truncatedChildCount++;
        }

        return new TreeNode(typeName, node.Count, "paths", children.Count > 0 ? children : null, truncatedChildCount);
    }

    private sealed class RootPathTrieNode
    {
        public int Count;
        public readonly Dictionary<string, RootPathTrieNode> Children = new(StringComparer.Ordinal);
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
                Cell(kind.PctOfManagedHeap.ToString("F1") + "%"),
                Cell(kind.IsExactRetainedBytes ? "Yes" : "No"),
                Cell((kind.Gen0Fraction * 100.0).ToString("F1") + "%"),
                Cell((kind.Gen1Fraction * 100.0).ToString("F1") + "%"),
                Cell((kind.Gen2Fraction * 100.0).ToString("F1") + "%"),
                Cell((kind.LohFraction * 100.0).ToString("F1") + "%")));
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
                Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                Cell(root.RetainedBytesIsExact ? "Yes" : "No")));
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