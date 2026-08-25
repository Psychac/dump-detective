using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadStackClusterSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopSignaturesToShow = 5;
    // Report-width display limits (§9.24 D5) — the analyzer emits complete, uncapped cluster and
    // sample-thread-ID data; these are render-layer-only slicing constants, not exactness knobs.
    private const int TopClustersToShow = 12;
    private const int MaxSampleIdsPerClusterToShow = 8;

    public string AnalyzerName => "Thread Stack Signature Clustering";
    public int SortOrder => 110;

    public bool CanHandle(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ThreadStackClusterDomainResult)result;
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["alive_threads"] = new NumericMetricValue(d.AliveThreadCount, MetricUnit.Count),
            ["unique_signatures"] = new NumericMetricValue(d.UniqueClusters, MetricUnit.Count),
            ["singleton_signatures"] = new NumericMetricValue(d.SingletonSignatures, MetricUnit.Count),
            ["signature_diversity_pct"] = new NumericMetricValue(d.DiversityPercent, MetricUnit.Percent, $"{d.DiversityPercent:F1}%"),
        };
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        // Only emit TopClusterSignatures text blocks if typed TopClusters slot is not available.
        // When TopClusters is populated, the typed slot has all the information (signature, count, thread IDs),
        // making text blocks redundant.
        if ((d.TopClusters == null || d.TopClusters.Count == 0) && d.TopClusterSignatures.Count > 0)
        {
            int sigLimit = Math.Min(d.TopClusterSignatures.Count, TopSignaturesToShow);
            for (int i = 0; i < sigLimit; i++)
                blocks.Add(T(FormatHelper.TruncateString(d.TopClusterSignatures[i], 120)));
        }

        blocks.Add(d.DiversityPercent <= 25
            ? T("Low signature diversity; large clusters may indicate coordinated blocking/contention.")
            : T("Signature diversity suggests varied active work."));

        // Typed StackClusters slot — full cluster/sample data already computed by the analyzer;
        // only the display width (how many clusters, how many sample IDs per cluster) is capped
        // here (§9.24 D5).
        var stackClusters = new List<StackCluster>();
        var clusters = d.TopClusters ?? [];
        int clusterLimit = Math.Min(clusters.Count, TopClustersToShow);
        for (int i = 0; i < clusterLimit; i++)
        {
            var cluster = clusters[i];
            int idLimit = Math.Min(cluster.SampleOsThreadIds.Count, MaxSampleIdsPerClusterToShow);
            var osIds = new List<string>(idLimit);
            for (int j = 0; j < idLimit; j++)
                osIds.Add($"0x{cluster.SampleOsThreadIds[j]:X}");
            stackClusters.Add(new StackCluster(
                ThreadCount: cluster.Count,
                OsThreadIds: osIds,
                Signature:   cluster.Signature,
                Truncated:   cluster.SampleOsThreadIds.Count > idLimit,
                FrameworkPattern: cluster.FrameworkPattern));
        }

        var frameHotspots = d.TopFrameHotspots ?? [];
        if (frameHotspots.Count > 0)
        {
            var hsRows = new List<TableRow>(frameHotspots.Count);
            for (int i = 0; i < frameHotspots.Count; i++)
                hsRows.Add(new TableRow([Cell(frameHotspots[i].Name), Cell($"{frameHotspots[i].Count:N0}", frameHotspots[i].Count)]));
            compactTables.Add(STCompact("Top frame hotspots (cross-cluster)", new[] { CH("Frame"), CH("Count", "number") }, hsRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // P3-2: typed TreeWidgets slot — shared-prefix cluster tree, rendered by the shared
        // collapsible tree widget (docs/refactor/collapsible-tree-widget-design.md).
        var treeWidgets = new List<TreeWidget>();
        if (d.ClusterTreeRoots is { Count: > 0 })
        {
            var roots = d.ClusterTreeRoots.Select(BuildTreeNode).ToArray();
            bool anyTruncated = roots.Any(HasTruncation);
            treeWidgets.Add(new TreeWidget("Cluster tree (shared blocking point)", roots, anyTruncated));
        }

        // Typed Artifacts slot
        var artifacts = new List<AnalyzerArtifact>();
        foreach (var a in d.Artifacts ?? [])
        {
            string instructions = a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "Pretty JSON — open in VS Code or any JSON viewer."
                : a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase)
                    ? $"NDJSON + gzip (streamable). Inspect with: gzip -cd {a.FileName} | jq -C '.' or open in 7-Zip/VS Code after extraction."
                    : "Analyzer export file.";
            artifacts.Add(new AnalyzerArtifact(a.FileName, instructions));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: AnalyzerName,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            StackClusters: stackClusters.Count > 0 ? stackClusters : null,
            TreeWidgets:   treeWidgets.Count > 0 ? treeWidgets : null,
            Artifacts:     artifacts.Count > 0 ? artifacts : null);
    }

    private static TreeNode BuildTreeNode(ThreadClusterTreeNode node) =>
        new(
            node.FrameLabel,
            node.Count,
            "threads",
            node.Children.Count > 0 ? node.Children.Select(BuildTreeNode).ToArray() : null,
            node.TruncatedChildCount,
            node.IsChain);

    private static bool HasTruncation(TreeNode node) =>
        node.TruncatedChildCount > 0 || (node.Children?.Any(HasTruncation) ?? false);
}
