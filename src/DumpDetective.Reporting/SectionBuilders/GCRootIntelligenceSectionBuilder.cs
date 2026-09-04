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
            ConfidenceScoring.F(roots.SubgraphWalkCapped, 0.20, $"Root-owned subgraph walk was capped ({roots.SubgraphWalkCappedCount:N0} subgraph(s) truncated)."));

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
            ["subgraph_walk_capped"] = new TextMetricValue(roots.SubgraphWalkCapped ? $"Yes ({roots.SubgraphWalkCappedCount:N0} capped)" : "No"),
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

        // ── Root-owned subgraphs: typed RootOwnedSubgraphGroups slot ────────
        var subgraphGroups = new List<RootOwnedSubgraphGroup>();

        if (roots.RootOwnedSubgraphs.Count > 0)
        {
            var subgraphGroupings = roots.RootOwnedSubgraphs
                .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key);

            foreach (var group in subgraphGroupings)
            {
                var subgraphsInGroup = group.OrderBy(p => p.SubgraphNodeCount).Take(3).ToArray();
                bool anyGroupCapped = false;
                for (int pi = 0; pi < subgraphsInGroup.Length; pi++)
                    if (subgraphsInGroup[pi].WasCapped) { anyGroupCapped = true; break; }

                var typedSubgraphs = new List<RootOwnedSubgraph>(subgraphsInGroup.Length);
                for (int pi = 0; pi < subgraphsInGroup.Length; pi++)
                {
                    var p = subgraphsInGroup[pi];
                    typedSubgraphs.Add(new RootOwnedSubgraph(
                        RootKind:      p.RootKind,
                        TargetAddress: $"0x{p.TargetAddress:X8}",
                        SubgraphNodeCount: p.SubgraphNodeCount,
                        WasCapped:     p.WasCapped,
                        Hops:          p.SubgraphTypeNames,
                        EstimatedRetainedBytes:  p.EstimatedRetainedBytes,
                        RetainedSizeWasWalked:   p.RetainedSizeWasWalked,
                        RetainedSizeIsExact:     p.RetainedSizeIsExact));
                }

                subgraphGroups.Add(new RootOwnedSubgraphGroup(
                    TargetType:      group.Key,
                    TargetTypeShort: TrimTypeName(group.Key),
                    TotalSubgraphCount: group.Count(),
                    AnyCapped:       anyGroupCapped,
                    Subgraphs:       typedSubgraphs));
            }

            if (roots.SubgraphWalkCapped)
                blocks.Add(T($"Root-owned subgraph walk was capped ({roots.SubgraphWalkCappedCount:N0} subgraph(s) truncated) — some types may have incomplete subgraphs."));
        }

        // P3-4 (docs/analysis/phase1/gcroot-analyzer-audit.md): typed TreeWidgets slot — collapses
        // RootOwnedSubgraphGroups entries that share a common structure near the target end into one
        // shared-prefix tree per group, instead of one independent card per subgraph (rendered by
        // the same shared collapsible tree widget ThreadStackClusterAnalyzer's cluster tree uses —
        // see docs/refactor/collapsible-tree-widget-design.md). RootOwnedSubgraphGroups above is
        // unchanged and still emitted for consumers that only want the flat per-subgraph view.
        List<TreeWidget>? treeWidgets = roots.RootOwnedSubgraphs.Count > 0 ? BuildRootOwnedSubgraphTreeWidgets(roots.RootOwnedSubgraphs) : null;

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            RootOwnedSubgraphGroups: subgraphGroups.Count > 0 ? subgraphGroups : null,
            TreeWidgets: treeWidgets);
    }

    // ── P3-4: shared-prefix tree over each RootOwnedSubgraphGroup's forward-walk hop sequences ──
    // Mirrors ThreadStackClusterAnalyzer.BuildClusterTree's trie-merge shape (see that method's
    // docs/refactor/collapsible-tree-widget-design.md reference), scoped to
    // RootOwnedSubgraphFinding.SubgraphTypeNames instead of stack-frame signatures. Every subgraph
    // in a group already shares hop[0] by construction (grouped by TargetTypeName), so that hop
    // becomes the group's tree root label and only hops[1..] are merged.
    private const int MaxSubgraphTreeNodes = 400;
    private const int MaxSubgraphTreeChildren = 8;

    // Safety bound (not a display truncation) on rendered TreeNode nesting depth — same reasoning
    // as ThreadStackClusterAnalyzer.MaxTreeDepth. RootOwnedSubgraphFinding.SubgraphTypeNames comes
    // from BoundedGraphWalk.CollectForwardTypeNames, a breadth-first walk capped at 500 *nodes*
    // (PathWalkMaxNodes), not 20 hops — for a near-linear reachable subgraph (e.g. a long List/
    // LinkedList chain) that BFS visits in mostly one direction, so SubgraphTypeNames can carry
    // close to 500 hops. Left uncapped here, an unbranched run of that length previously nested
    // ~500 levels of TreeNode.Children and tripped System.Text.Json's MaxDepth guard ("possible
    // object cycle detected") on real dumps.
    private const int MaxSubgraphTreeDepth = 64;

    private static List<TreeWidget> BuildRootOwnedSubgraphTreeWidgets(IReadOnlyList<RootOwnedSubgraphFinding> subgraphFindings)
    {
        var byTargetType = subgraphFindings
            .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        int nodeBudget = MaxSubgraphTreeNodes;
        var groupRoots = new List<TreeNode>();
        bool anyTruncated = false;

        foreach (var group in byTargetType)
        {
            if (nodeBudget <= 0) { anyTruncated = true; break; }
            nodeBudget--;

            var trieRoot = new SubgraphTrieNode();
            int subgraphCount = 0;
            foreach (RootOwnedSubgraphFinding finding in group)
            {
                subgraphCount++;
                SubgraphTrieNode node = trieRoot;
                for (int i = 1; i < finding.SubgraphTypeNames.Count; i++)
                {
                    node = GetOrAddChild(node, finding.SubgraphTypeNames[i]);
                    node.Count++;
                }
            }

            List<KeyValuePair<string, SubgraphTrieNode>> orderedChildren = OrderChildrenByCountDescending(trieRoot.Children);
            var children = new List<TreeNode>(Math.Min(orderedChildren.Count, MaxSubgraphTreeChildren));
            int childTruncated = 0;
            for (int i = 0; i < orderedChildren.Count; i++)
            {
                if (children.Count < MaxSubgraphTreeChildren && nodeBudget > 0)
                    children.Add(ConvertSubgraphTrieNode(orderedChildren[i].Key, orderedChildren[i].Value, ref nodeBudget, depth: 0));
                else
                    childTruncated++;
            }
            if (childTruncated > 0)
                anyTruncated = true;

            groupRoots.Add(new TreeNode(TrimTypeName(group.Key), subgraphCount, "subgraphs",
                children.Count > 0 ? children : null, childTruncated));
        }

        if (!anyTruncated)
            anyTruncated = groupRoots.Any(HasTruncation);

        return
        [
            new TreeWidget("Root-owned subgraph shapes (shared structure collapsed)", groupRoots, anyTruncated)
        ];
    }

    private static SubgraphTrieNode GetOrAddChild(SubgraphTrieNode node, string typeName)
    {
        if (!node.Children.TryGetValue(typeName, out SubgraphTrieNode? child))
        {
            child = new SubgraphTrieNode();
            node.Children[typeName] = child;
        }
        return child;
    }

    private static List<KeyValuePair<string, SubgraphTrieNode>> OrderChildrenByCountDescending(Dictionary<string, SubgraphTrieNode> children)
    {
        var ordered = new List<KeyValuePair<string, SubgraphTrieNode>>(children);
        ordered.Sort(static (a, b) =>
        {
            int byCount = b.Value.Count.CompareTo(a.Value.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });
        return ordered;
    }

    private static TreeNode ConvertSubgraphTrieNode(string typeName, SubgraphTrieNode node, ref int nodeBudget, int depth)
    {
        nodeBudget--;

        List<KeyValuePair<string, SubgraphTrieNode>> orderedChildren = OrderChildrenByCountDescending(node.Children);

        if (depth >= MaxSubgraphTreeDepth)
            return new TreeNode(typeName, node.Count, "subgraphs", null, orderedChildren.Count);

        var children = new List<TreeNode>(Math.Min(orderedChildren.Count, MaxSubgraphTreeChildren));
        int truncatedChildCount = 0;
        for (int i = 0; i < orderedChildren.Count; i++)
        {
            if (children.Count < MaxSubgraphTreeChildren && nodeBudget > 0)
                children.Add(ConvertSubgraphTrieNode(orderedChildren[i].Key, orderedChildren[i].Value, ref nodeBudget, depth + 1));
            else
                truncatedChildCount++;
        }

        return new TreeNode(typeName, node.Count, "subgraphs", children.Count > 0 ? children : null, truncatedChildCount);
    }

    private static bool HasTruncation(TreeNode node) =>
        node.TruncatedChildCount > 0 || (node.Children?.Any(HasTruncation) ?? false);

    private sealed class SubgraphTrieNode
    {
        public int Count;
        public readonly Dictionary<string, SubgraphTrieNode> Children = new(StringComparer.Ordinal);
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