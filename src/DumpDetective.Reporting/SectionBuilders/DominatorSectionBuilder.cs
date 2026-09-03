using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>A3 — Dominator Analysis and retention hotspots. Source: <see cref="DominatorDomainResult"/>.</summary>
internal sealed class DominatorSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Dominator Analysis";
    public string DisplayTitle => "Dominator Analysis";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is DominatorDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DominatorDomainResult)result;
        var (confidenceScore, caveats) = ConfidenceScoring.Compute(0.75,
            ConfidenceScoring.F(d.ObjectScanCapped, 0.15, "Object scan was capped; retention counts may be partial."),
            ConfidenceScoring.F(d.ReferenceCountingSkipped, 0.20, "Reference counting was skipped; results are estimated."),
            ConfidenceScoring.F(d.ApproximatedReferenceAddresses > 0, 0.10, $"{d.ApproximatedReferenceAddresses:N0} reference addresses have approximated (bounded-error) counts."),
            ConfidenceScoring.F(d.CrossTypeOverlapInstanceScanCapped, 0.05, "Cross-type overlap instance scan was capped; overlap counts may undercount."));

        var compactTables = new List<CompactTable>();
        var treeWidgets = new List<TreeWidget>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(confidenceScore,
                new[] { "Retained bytes are bounded BFS estimates, not a true Lengauer-Tarjan dominator tree." }
                    .Concat(caveats)
                    .ToArray()),
        };

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["candidate_count"] = new NumericMetricValue(d.CandidateCount, MetricUnit.Count),
            ["analyzed_count"] = new NumericMetricValue(d.AnalyzedCount, MetricUnit.Count),
            ["total_retained_est"] = new NumericMetricValue((double)Math.Min(d.TotalEstimatedRetainedBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TotalEstimatedRetainedBytes)),
            ["retention_pressure_ratio"] = d.TotalHeapBytes > 0
                ? new NumericMetricValue((double)d.TotalEstimatedRetainedBytes / d.TotalHeapBytes, MetricUnit.Ratio,
                    $"{(double)d.TotalEstimatedRetainedBytes / d.TotalHeapBytes:P1}")
                : new TextMetricValue("N/A"),
            ["max_bfs_breadth"] = new NumericMetricValue(d.MaxBreadth, MetricUnit.Count),
            ["max_bfs_depth"] = new NumericMetricValue(d.MaxDepth, MetricUnit.Count),
            ["highly_referenced_objects"] = new NumericMetricValue(d.HighlyReferencedObjectCount, MetricUnit.Count),
            ["top_retained_total"] = new NumericMetricValue((double)Math.Min(d.TopHighlyReferencedTotalBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TopHighlyReferencedTotalBytes)),
            ["approximated_ref_addresses"] = new NumericMetricValue(d.ApproximatedReferenceAddresses, MetricUnit.Count),
        };

        if (d.TopDominatorTypes.Count > 0)
        {
            blocks.Add(T($"Retained bytes are estimated with a bounded BFS over {d.AnalyzedCount:N0} suspects (breadth cap {d.MaxBreadth:N0}, depth cap {d.MaxDepth:N0})."));

            compactTables.Add(STCompact(
                "Top dominator suspects by retained bytes",
                new[] { CH("Type"), CH("Objects","number"), CH("Gen2","number"), CH("Shallow","bytes"), CH("LOH","bytes"), CH("Retained","bytes"), CH("Ratio", "number", "permille"), CH("Avg Size","bytes"), CH("Capped?"), CH("Sample Addr") },
                d.TopDominatorTypes.Take(d.MaxTopDominatorTypesToShow).Select(type => R(
                    type.TypeName,
                    type.Count,
                    type.Gen2Count > 0 ? type.Gen2Count : null,
                    type.TotalBytes,
                    type.LohBytes > 0 ? type.LohBytes : null,
                    type.EstimatedRetainedBytes > 0 ? type.EstimatedRetainedBytes : null,
                    (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000),
                    type.AverageSize > 0 ? type.AverageSize : null,
                    type.WasCapped ? "Yes" : null,
                    $"0x{type.SampleAddress:X}")).ToArray()));
            if (d.TotalEstimatedRetainedBytes > 0)
            {
                compactTables.Add(STCompact(
                    "Dominator impact per-mille (of total estimated retained)",
                    new[] { CH("Type"), CH("Est. Retained","bytes"), CH("Per-mille", "number", "permille") },
                    d.TopDominatorTypes.Take(d.MaxTopDominatorTypesToShow).Select(type => R(
                        type.TypeName,
                        type.EstimatedRetainedBytes > 0 ? type.EstimatedRetainedBytes : null,
                        type.EstimatedRetainedBytes == 0 ? null : (double)type.EstimatedRetainedBytes * 1000 / d.TotalEstimatedRetainedBytes)).ToArray()));
            }

            // GC-focused sub-table: isolates types with a meaningful Gen2 or LOH footprint from the
            // Gen0/1 noise mixed into the main dominator table above — immediately actionable for
            // GC-pressure investigations (long-lived / large-object retention).
            var gen2LohTypes = d.TopDominatorTypes
                .Where(type => type.Gen2Count > 0 || type.LohBytes > 0)
                .OrderByDescending(type => type.Gen2Count)
                .ThenByDescending(type => type.LohBytes)
                .Take(d.MaxTopDominatorTypesToShow)
                .ToArray();
            if (gen2LohTypes.Length > 0)
            {
                // §Report integration (dominator-tree-lengauer-tarjan.md §Architecture "Output
                // model"): use the exact Lengauer-Tarjan retained-bytes total for a type when the
                // exact path succeeded for this run; otherwise fall back to the existing bounded-BFS
                // estimate unchanged. Only this sub-table's "Retained" column is affected — the main
                // dominator-suspects and highly-referenced-objects tables above/below are untouched.
                bool anyExact = d.ExactRetainedBytesByTypeName is { Count: > 0 };
                if (anyExact)
                    blocks.Add(T("Gen2/LOH retained bytes below are exact (Lengauer-Tarjan dominator tree) for this run."));

                // Audit P2 (docs/refactor/narrative-interpretation-text-design.md, first adopter):
                // interprets the top-ranked candidate's retained/shallow ratio rather than every
                // row — gen2LohTypes is already sorted by Gen2Count/LohBytes descending, so the
                // first entry is the sub-table's most notable row, and naming it keeps the blurb
                // unambiguous about which row it describes despite the table having many.
                TypeSnapshot topGen2LohType = gen2LohTypes[0];
                ulong topGen2LohRetained = d.ExactRetainedBytesByTypeName is { } topExact && topExact.TryGetValue(topGen2LohType.TypeName, out ulong topExactRetained)
                    ? topExactRetained
                    : topGen2LohType.EstimatedRetainedBytes;
                string topGen2LohRatioText = FormatRatio(topGen2LohRetained, topGen2LohType.TotalBytes);
                InterpretationBlock? ratioInterpretation = Interpret(RatioValue(topGen2LohRetained, topGen2LohType.TotalBytes),
                    (3.0, $"{topGen2LohType.TypeName}: retained ≫ shallow ({topGen2LohRatioText}) — holds a large external graph."),
                    (1.2, $"{topGen2LohType.TypeName}: retained > shallow ({topGen2LohRatioText}) — holds some external references."),
                    (0.0, $"{topGen2LohType.TypeName}: retained ≈ shallow ({topGen2LohRatioText}) — largely self-contained."));
                if (ratioInterpretation is not null)
                    blocks.Add(ratioInterpretation);

                compactTables.Add(STCompact(
                    "Gen2 / LOH dominator suspects",
                    new[] { CH("Type"), CH("Objects","number"), CH("Gen2","number"), CH("Gen2 %", "number", "percent"), CH("LOH","bytes"), CH("Retained","bytes") },
                    gen2LohTypes.Select(type =>
                    {
                        ulong retained = d.ExactRetainedBytesByTypeName is { } exact && exact.TryGetValue(type.TypeName, out ulong exactRetained)
                            ? exactRetained
                            : type.EstimatedRetainedBytes;
                        return R(
                            type.TypeName,
                            type.Count,
                            type.Gen2Count > 0 ? type.Gen2Count : null,
                            type.Count == 0 ? 0.0 : type.Gen2Count * 100.0 / type.Count,
                            type.LohBytes > 0 ? type.LohBytes : null,
                            retained > 0 ? retained : null);
                    }).ToArray()));

                // P3-3: per-type dominance chain (A dominates B dominates ... dominates the
                // sample object), reusing the shared collapsible tree widget
                // (docs/refactor/collapsible-tree-widget-design.md — this is its third planned
                // adopter). Scoped to the Gen2/LOH sub-table only, same deliberate scoping as
                // Audit Area 8 item 1 — not every table gets a chain.
                if (d.DominatorChainsByTypeName is { Count: > 0 } chainsByTypeName)
                {
                    var chainRoots = new List<TreeNode>(gen2LohTypes.Length);
                    foreach (TypeSnapshot type in gen2LohTypes)
                    {
                        if (chainsByTypeName.TryGetValue(type.TypeName, out IReadOnlyList<DominatorChainHop>? chain) && chain.Count > 0)
                            chainRoots.Add(BuildDominatorChainNode(chain, 0));
                    }

                    if (chainRoots.Count > 0)
                        treeWidgets.Add(new TreeWidget("Gen2 / LOH dominance chains", chainRoots));
                }

                // Audit P2 (docs/analysis/phase1/dominator-analyzer-audit.md): inline root-chain
                // summary — "why is this alive," a fourth adopter of the shared tree widget,
                // complementing the dominance chain above ("who retains this") rather than
                // replacing it. Scoped to Gen2/LOH candidates same as everywhere else on this path;
                // a missing entry means no path from any GC root was found within the search's own
                // bounds, not that the object is unrooted (RootPathFinder is a bounded heuristic
                // search, not exhaustive).
                if (d.RootChainsByTypeName is { Count: > 0 } rootChainsByTypeName)
                {
                    // Audit P3 "dedup rendered root chains": several Gen2/LOH candidates can
                    // genuinely share the same ancestor path (e.g. a dozen cache-entry types all
                    // held by the same static cache) — grouping by shared shape before rendering
                    // keeps the widget from becoming as noisy at scale as the bare-address baseline
                    // it replaced. Grouped by (root kind, ancestor hops) — every hop except the
                    // last, since the last hop is always the candidate's own type and differs by
                    // construction between different candidates.
                    var rootChainGroups = new List<List<(string TypeName, RootChainSummary Summary)>>();
                    var groupIndexByShapeKey = new Dictionary<string, int>(StringComparer.Ordinal);
                    int ungroupedCounter = 0;
                    foreach (TypeSnapshot type in gen2LohTypes)
                    {
                        if (!rootChainsByTypeName.TryGetValue(type.TypeName, out RootChainSummary? summary) || summary.HopTypeNames.Count == 0)
                            continue;

                        // A chain with no ancestor hops (the candidate is itself a direct GC root)
                        // has nothing shape-worthy to share with another candidate — never grouped,
                        // even with another equally-bare direct root of the same kind.
                        string shapeKey = summary.HopTypeNames.Count > 1
                            ? summary.RootKind + "|" + string.Join("→", summary.HopTypeNames.Take(summary.HopTypeNames.Count - 1))
                            : $"__ungrouped_{ungroupedCounter++}";

                        if (!groupIndexByShapeKey.TryGetValue(shapeKey, out int groupIndex))
                        {
                            groupIndex = rootChainGroups.Count;
                            groupIndexByShapeKey[shapeKey] = groupIndex;
                            rootChainGroups.Add(new List<(string, RootChainSummary)>());
                        }

                        rootChainGroups[groupIndex].Add((type.TypeName, summary));
                    }

                    var rootChainRoots = new List<TreeNode>(rootChainGroups.Count);
                    foreach (List<(string TypeName, RootChainSummary Summary)> group in rootChainGroups)
                        rootChainRoots.Add(BuildRootChainNode(group, 0));

                    if (rootChainRoots.Count > 0)
                        treeWidgets.Add(new TreeWidget("Gen2 / LOH root paths", rootChainRoots));
                }

                // §8 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): sample-
                // based cross-type overlap — explains why two candidate types' *exclusive* retained
                // bytes can both look small/zero even though they show up together as high-score
                // suspects (one's sampled instance is nested entirely inside the other's). Only
                // rows where a containing candidate was actually found are listed, not the full
                // Gen2/LOH set, since most candidates are independent top-level retainers.
                if (d.ContainingTypeNameByTypeName is { Count: > 0 } containingTypeNameByTypeName)
                {
                    var overlapRows = new List<CompactRow>();
                    foreach (TypeSnapshot type in gen2LohTypes)
                    {
                        if (containingTypeNameByTypeName.TryGetValue(type.TypeName, out string? containingTypeName))
                            overlapRows.Add(R(type.TypeName, containingTypeName));
                    }

                    if (overlapRows.Count > 0)
                    {
                        compactTables.Add(STCompact(
                            "Shared subgraph overlap (sample-based)",
                            new[] { CH("Type"), CH("Fully contained within") },
                            overlapRows.ToArray()));
                    }
                }

                // §8b/§8c: full-population cross-type overlap — how many instances of a Gen2/LOH
                // candidate type are nested inside another candidate type's subgraph (§8b), and the
                // exact retained bytes contributed by that pair's "topmost" instances (§8c) — not
                // just whether the one sampled instance happens to be, like §8a above. Complements
                // §8a rather than replacing it: §8a can be empty for a pair while §8b/§8c still find
                // overlap among the type's other instances, or vice versa if the depth cap was hit.
                // "Retained bytes" can legitimately be 0 while "Instances" is positive — see
                // CrossTypeOverlapPair's own doc comment for why that's an honest outcome, not a bug.
                if (d.CrossTypeOverlapPairs is { Count: > 0 } overlapPairs)
                {
                    var gen2LohTypeNames = new HashSet<string>(gen2LohTypes.Select(t => t.TypeName), StringComparer.Ordinal);
                    var populationRows = overlapPairs
                        .Where(p => gen2LohTypeNames.Contains(p.TypeName))
                        .OrderByDescending(p => p.ContainedRetainedBytes)
                        .ThenByDescending(p => p.ContainedInstanceCount)
                        .Select(p => R(p.TypeName, p.ContainingTypeName, p.ContainedInstanceCount, p.ContainedRetainedBytes > 0 ? p.ContainedRetainedBytes : null))
                        .ToArray();

                    if (populationRows.Length > 0)
                    {
                        compactTables.Add(STCompact(
                            "Cross-type retained overlap",
                            new[] { CH("Type"), CH("Contained within"), CH("Instances", "number"), CH("Retained", "bytes") },
                            populationRows));
                    }
                }
            }
        }

        if (d.TopHighlyReferencedObjects is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Highly referenced objects",
                new[] { CH("Address"), CH("Type"), CH("Size","bytes"), CH("Incoming Refs","number"), CH("Est. Retained","bytes") },
                d.TopHighlyReferencedObjects.Take(d.MaxTopDominatorTypesToShow).Select(o => R(new object?[] { $"0x{o.Address:X}", o.TypeName, o.Size, o.IncomingReferences, o.EstimatedRetainedBytes > 0 ? o.EstimatedRetainedBytes : null })).ToArray()));
        }

        if (d.TopRetentionTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Top retention types",
                new[] { CH("Type"), CH("Objects","number"), CH("Footprint","bytes"), CH("Total Incoming Refs","number"), CH("Max Incoming Refs","number"), CH("Est. Retained","bytes"), CH("Ratio", "number", "ratio") },
                d.TopRetentionTypes.Take(d.MaxTopDominatorTypesToShow).Select(t => R(new object?[] { t.TypeName, t.ObjectCount, t.TotalBytes, t.TotalIncomingReferences, t.MaxIncomingReferences, t.EstimatedRetainedBytes > 0 ? t.EstimatedRetainedBytes : null, RatioValue(t.EstimatedRetainedBytes, t.TotalBytes) })).ToArray()));
        }

        if (d.FanInHistogram is { Count: > 0 } fanInHistogram)
        {
            long totalFanIn = 0;
            for (int i = 0; i < fanInHistogram.Count; i++) totalFanIn += fanInHistogram[i].ObjectCount;

            compactTables.Add(STCompact(
                "Incoming-reference (fan-in) distribution",
                new[] { CH("Incoming Refs Range"), CH("Objects","number"), CH("% of Objects", "number", "percent") },
                fanInHistogram.Select(b => R(
                    b.ReferenceCountRange,
                    b.ObjectCount,
                    totalFanIn == 0 ? 0.0 : b.ObjectCount * 100.0 / totalFanIn)).ToArray()));
        }

        if (caveats.Count > 0)
            blocks.Add(T("Caveats: " + string.Join(" ", caveats)));

        // Audit P3 "Shared Next steps" (docs/analysis/phase1/dominator-analyzer-audit.md):
        // dominator-suspect bytes explain *how much* is retained; GC Root Analysis explains *why*
        // it's rooted at all, and Reference Chain Analysis walks the concrete path to it — the
        // natural next two stops once a suspect type is identified here.
        NextStepsBlock? nextSteps = NextSteps(
            ("Check why these types are rooted", "GCRootAnalyzer"),
            ("Trace concrete reference chains to a suspect", "ReferenceChainAnalyzer"));
        if (nextSteps is not null)
            blocks.Add(nextSteps);

        return new AnalyzerDetailSection(
            AnalyzerName: "Dominator Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            TreeWidgets: treeWidgets.Count > 0 ? treeWidgets : null);
    }

    private static new string FormatRatio(ulong retained, ulong shallow)
        => shallow == 0 ? "—" : $"{(double)retained / shallow:F2}x";

    private static new double RatioValue(ulong retained, ulong shallow)
        => shallow == 0 ? 0.0 : (double)retained / shallow;

    // P3-3: converts a root-most-first DominatorChainHop list into nested single-child TreeNodes.
    // Retained bytes are baked into the label text rather than TreeNode.Count (int?) — retained
    // bytes routinely exceed int.MaxValue on large heaps, and Count would silently overflow.
    private TreeNode BuildDominatorChainNode(IReadOnlyList<DominatorChainHop> hops, int index)
    {
        DominatorChainHop hop = hops[index];
        bool isSentinel = hop.Address == 0;
        string label = isSentinel ? hop.TypeName : $"{hop.TypeName} — {FormatBytes(hop.RetainedBytes)} retained";

        TreeNode? child = index + 1 < hops.Count ? BuildDominatorChainNode(hops, index + 1) : null;
        return new TreeNode(
            Label: label,
            Children: child is not null ? new[] { child } : null,
            IsChain: true);
    }

    // Audit P2: converts a root-most-first RootChainSummary.HopTypeNames list into nested
    // single-child TreeNodes, same shape as BuildDominatorChainNode above. The root kind is baked
    // into the first hop's label (the natural place to show "why" this chain starts) rather than a
    // separate tree level; a truncated search is called out there too rather than silently omitted.
    // Audit P2/P3: converts a root-most-first hop list into nested single-child TreeNodes, same
    // shape as BuildDominatorChainNode. Operates on a *group* of (type, summary) pairs sharing an
    // identical chain shape rather than a single summary — the representative (group[0]) supplies
    // the shared root kind/ancestor hops (identical across the whole group by construction, since
    // grouping already matched on them), while the final (self) hop's label folds in every group
    // member's type name instead of just one when the group has more than one, so N candidates
    // sharing a chain render as one path with a "×N types" leaf instead of N near-identical trees.
    private TreeNode BuildRootChainNode(IReadOnlyList<(string TypeName, RootChainSummary Summary)> group, int index)
    {
        RootChainSummary representative = group[0].Summary;
        IReadOnlyList<string> hops = representative.HopTypeNames;
        bool isFinalHop = index == hops.Count - 1;

        string label;
        if (index == 0)
        {
            label = representative.Truncated
                ? $"[{representative.RootKind}] {hops[index]} (search truncated for other roots)"
                : $"[{representative.RootKind}] {hops[index]}";
        }
        else if (isFinalHop && group.Count > 1)
        {
            label = FormatDedupedLeafLabel(group);
        }
        else
        {
            label = hops[index];
        }

        TreeNode? child = index + 1 < hops.Count ? BuildRootChainNode(group, index + 1) : null;
        return new TreeNode(
            Label: label,
            Children: child is not null ? new[] { child } : null,
            IsChain: true);
    }

    private const int MaxDedupedRootChainNamesShown = 5;

    private static string FormatDedupedLeafLabel(IReadOnlyList<(string TypeName, RootChainSummary Summary)> group)
    {
        var shown = new List<string>(Math.Min(group.Count, MaxDedupedRootChainNamesShown));
        for (int i = 0; i < group.Count && i < MaxDedupedRootChainNamesShown; i++)
            shown.Add(group[i].TypeName);

        string suffix = group.Count > MaxDedupedRootChainNamesShown
            ? $", +{group.Count - MaxDedupedRootChainNamesShown} more"
            : "";
        return $"×{group.Count} types — same chain: {string.Join(", ", shown)}{suffix}";
    }
}
