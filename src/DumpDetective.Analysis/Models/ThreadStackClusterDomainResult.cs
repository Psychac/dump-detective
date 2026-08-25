using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Thread Stack Clusters

internal sealed record ThreadStackClusterDomainResult(
    int AliveThreadCount,
    int UniqueClusters,
    int SingletonSignatures,
    double DiversityPercent,
    IReadOnlyList<string> TopClusterSignatures,
    IReadOnlyList<ThreadClusterSnapshot>? TopClusters = null,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null,
    IReadOnlyList<NameCountEntry>? TopFrameHotspots = null,
    IReadOnlyList<ThreadClusterTreeNode>? ClusterTreeRoots = null) : AnalyzerDomainResult;

/// <summary>
/// <paramref name="SampleOsThreadIds"/>/<paramref name="SampleManagedThreadIds"/> are the complete
/// per-cluster thread ID lists (§9.24) — the "Sample" prefix is kept only for artifact-schema
/// stability (JSON/NDJSON cluster exports), not because the list is truncated. Display-width
/// truncation, if any, happens at the render layer.
/// </summary>
internal sealed record ThreadClusterSnapshot(
    int Count,
    IReadOnlyList<uint> SampleOsThreadIds,
    string Signature,
    int ThreadpoolWorkerCount = 0,
    int GcCount = 0,
    int FinalizerCount = 0,
    IReadOnlyList<int>? SampleManagedThreadIds = null,
    string? FrameworkPattern = null);

/// <summary>
/// P3-2: one node of the shared-prefix tree over cluster signatures, built innermost-frame-first
/// so branches converge on shared blocking points reached via different call sites. <paramref
/// name="Count"/> is the number of alive threads passing through this node (own leaf contribution
/// plus all descendants). <paramref name="IsChain"/> marks a node whose <paramref name="FrameLabel"/>
/// already represents a collapsed run of single-child ancestors (no cluster terminates along the
/// run), keeping straight-line call paths to one node instead of one node per frame.
/// </summary>
internal sealed record ThreadClusterTreeNode(
    string FrameLabel,
    int Count,
    IReadOnlyList<ThreadClusterTreeNode> Children,
    bool IsChain = false,
    int TruncatedChildCount = 0);
