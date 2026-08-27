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
            ConfidenceScoring.F(d.ApproximatedReferenceAddresses > 0, 0.10, $"{d.ApproximatedReferenceAddresses:N0} reference addresses have approximated (bounded-error) counts."));

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
}
