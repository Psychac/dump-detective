namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// §D6 memory budget for the exact dominator-tree path, as a **two-term cost model** — bytes per node
/// plus bytes per edge — rather than the flat bytes-per-node constant this originally used.
///
/// <para>A single per-node constant cannot express this cost, and measurement shows it failing in both
/// directions. Peak live bytes per node <i>fall</i> as the D8 leaf-fold rate rises and <i>rise</i> with
/// graph density, so the same constant is simultaneously too loose for one dump and too tight for
/// another:</para>
///
/// <list type="table">
///   <listheader><term>Dump</term><description>N / E / fold → measured peak</description></listheader>
///   <item><term>3.3 GB</term><description>6.69M / 17.37M / 32% → 0.87 GB = <b>140 B/node</b></description></item>
///   <item><term>25.6 GB</term><description>58.34M / 137.03M / 46% → 6.42 GB = <b>118 B/node</b></description></item>
/// </list>
///
/// <para>The larger dump is <i>cheaper per node</i> because it folds 46% of its leaves away instead of
/// 32%. A per-node constant calibrated on either dump misprices the other. This model prices both
/// correctly, which is what makes the budget a meaningful number instead of a guess.</para>
///
/// <para>Estimates are a deliberate upper bound: they assume <b>zero</b> leaf folding, since the fold
/// rate isn't knowable until the walk finishes. Real peak lands around 65% of the estimate on both
/// reference dumps (0.87/1.13 and 6.42/9.68). See
/// docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 5.</para>
/// </summary>
internal readonly struct ExactDominatorTreeBudget
{
    /// <summary>
    /// Peak-live bytes attributable to each node, summed over every simultaneously-live node-indexed
    /// array across the walk, leaf-fold, Lengauer-Tarjan and retained-bytes-rollup stages, assuming no
    /// folding. Derived stage-by-stage in the memory-profile doc § 3.2; the dominant stage is the LT +
    /// rollup phase at ~150 bytes/node.
    /// </summary>
    public const long DefaultBytesPerNode = 150;

    /// <summary>
    /// Peak-live bytes per edge: the forward and reverse CSR target arrays plus the
    /// virtual-root-extended reverse copy, at 4 bytes each, that coexist at the peak.
    /// </summary>
    public const long DefaultBytesPerEdge = 12;

    /// <summary>Zero or negative disables enforcement entirely.</summary>
    public long MaxBytes { get; }
    public long BytesPerNode { get; }
    public long BytesPerEdge { get; }

    public ExactDominatorTreeBudget(long maxBytes, long bytesPerNode = DefaultBytesPerNode, long bytesPerEdge = DefaultBytesPerEdge)
    {
        MaxBytes = maxBytes;
        BytesPerNode = bytesPerNode;
        BytesPerEdge = bytesPerEdge;
    }

    /// <summary>A budget that never rejects — for unit tests on synthetic graphs.</summary>
    public static ExactDominatorTreeBudget Unlimited => new(0);

    public bool IsEnforced => MaxBytes > 0;

    /// <summary>Conservative peak-live estimate for a graph of this size (assumes no leaf folding).</summary>
    public long EstimateBytes(long nodeCount, long edgeCount) =>
        (nodeCount * BytesPerNode) + (edgeCount * BytesPerEdge);

    public bool IsExceededBy(long nodeCount, long edgeCount) =>
        IsEnforced && EstimateBytes(nodeCount, edgeCount) > MaxBytes;

    /// <summary>
    /// Node count admitted if the graph were edgeless. Reporting only — never a real limit, since every
    /// reachable graph has edges. Included in log lines because a plain "cap N nodes" figure is what
    /// readers expect after years of the single-constant model.
    /// </summary>
    public long MaxNodesIgnoringEdges => BytesPerNode <= 0 ? long.MaxValue : MaxBytes / BytesPerNode;

    /// <summary>
    /// Node count admitted at a given average out-degree — the number that actually describes the
    /// limit. At the reference dumps' densities (2.35-2.60) this is roughly half
    /// <see cref="MaxNodesIgnoringEdges"/>.
    /// </summary>
    public long MaxNodesAtDensity(double averageOutDegree)
    {
        double perNode = BytesPerNode + (BytesPerEdge * averageOutDegree);
        return perNode <= 0 ? long.MaxValue : (long)(MaxBytes / perNode);
    }
}
