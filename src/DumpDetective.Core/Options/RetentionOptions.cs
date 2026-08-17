namespace DumpDetective.Core.Options;

public sealed class RetentionOptions
{
    public int TopFinalizerTypesToShow { get; init; } = 10;
    public int TopHighlyReferencedObjectsToShow { get; init; } = 15;

    public int HighReferenceThreshold { get; init; } = 50;
    public int MaxReferenceAddresses { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum number of objects subjected to full reference-field enumeration during
    /// the incoming-reference-count pass. Each traced object requires at least one
    /// <c>heap.GetObject()</c> call against the dump file, which is the primary
    /// bottleneck on large (multi-GB) dumps.
    /// Default: 2 000 000. Set to 0 to disable the limit (only safe on small dumps).
    /// When the limit is reached, ObjectScanCapped is set to true in the retention analyzer result.
    /// is set to <c>true</c> and a confidence note is emitted in the report.
    /// </summary>
    public int MaxLeakScanObjects { get; init; } = 2_000_000;

    /// <summary>
    /// Maximum number of candidate nodes explored during root path search.
    /// Limits the breadth of the BFS when searching for GC root paths.
    /// Default: 5 000. Set higher for more thorough evidence collection.
    /// </summary>
    public int MaxRootPathCandidateNodes { get; init; } = 5_000;

    /// <summary>
    /// Maximum depth (hops from root) explored during candidate search phase of root path finding.
    /// Default: 8.
    /// </summary>
    public int MaxRootPathCandidateDepth { get; init; } = 8;

    /// <summary>
    /// Maximum depth explored when expanding from a root node towards the target object.
    /// Default: 12.
    /// </summary>
    public int MaxRootPathExpansionDepth { get; init; } = 12;

    /// <summary>
    /// Fanout threshold above which a reference path is considered "large" and skipped
    /// to avoid exploring extremely high-connectivity clusters.
    /// Default: 100.
    /// </summary>
    public int RootPathLargeFanoutThreshold { get; init; } = 100;

    /// <summary>
    /// §D9 (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md): enables the exact
    /// Lengauer-Tarjan dominator-tree computation, protected by <see cref="ExactDominatorTreeMemoryBudgetBytes"/>'s
    /// mid-walk cap (§D6). Deliberately <b>not</b> branched per <see cref="AnalysisProfile"/> in
    /// <see cref="Preset"/> below — the profile system is expected to be simplified/consolidated
    /// later, and this flag is meant to stay independent of whatever shape it ends up in.
    /// Default <c>true</c>: exact mode is attempted by default, falling back to the existing
    /// top-K heuristic if the reachable population exceeds the memory budget.
    /// </summary>
    public bool EnableExactDominatorTree { get; init; } = true;

    /// <summary>
    /// §D6: the peak transient working set the exact dominator-tree computation is allowed. Enforced
    /// mid-walk against a two-term cost model — <c>150 bytes/node + 12 bytes/edge</c>, assuming zero
    /// D8 leaf folding — rather than a flat node cap, because per-node cost varies with both fold rate
    /// and graph density (see <c>ExactDominatorTreeBudget</c> for the measurements).
    ///
    /// <para><b>Default 20GB.</b> Sizing rationale, against the two measured reference dumps:</para>
    /// <list type="bullet">
    ///   <item>3.3GB dump (6.69M nodes / 17.37M edges): projects 1.13GB, real peak 0.87GB.</item>
    ///   <item>25.6GB dump (58.34M nodes / 137.03M edges): projects 9.68GB — <b>48% of budget</b>, so it
    ///         runs with comfortable margin. Real peak 6.42GB.</item>
    ///   <item>Headroom: admits roughly <b>119M reachable nodes</b> at an average out-degree of 2.5,
    ///         about 2x the largest dump measured. Real peak at that ceiling would be ~13GB, since the
    ///         model's no-folding assumption overshoots actual usage by ~1.5x.</item>
    /// </list>
    ///
    /// <para>The estimate is deliberately an upper bound: the fold rate isn't knowable until the walk
    /// completes, and both reference dumps land near 66% of their projection. Lower this on
    /// memory-constrained machines — over budget, the analyzer falls back to the top-K heuristic and
    /// says so in its log, it does not fail.</para>
    ///
    /// <para>A hard structural ceiling sits well above this default: the CSR target arrays are
    /// <c>int[]</c>, so the 2GB single-object limit caps the graph at ~537M edges (~215M nodes at
    /// out-degree 2.5) regardless of budget.</para>
    /// </summary>
    public long ExactDominatorTreeMemoryBudgetBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    public static RetentionOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new RetentionOptions
        {
            TopFinalizerTypesToShow = 5,
            TopHighlyReferencedObjectsToShow = 8,
            HighReferenceThreshold = 75,
            MaxReferenceAddresses = 250_000,
            MaxLeakScanObjects = 500_000,
            MaxRootPathCandidateNodes = 2_000,
            MaxRootPathCandidateDepth = 6,
            MaxRootPathExpansionDepth = 8,
            RootPathLargeFanoutThreshold = 50
        },
        AnalysisProfile.Full => new RetentionOptions
        {
            TopFinalizerTypesToShow = 25,
            TopHighlyReferencedObjectsToShow = 40,
            HighReferenceThreshold = 30,
            MaxReferenceAddresses = 2_000_000,
            MaxLeakScanObjects = 5_000_000,
            MaxRootPathCandidateNodes = 10_000,
            MaxRootPathCandidateDepth = 12,
            MaxRootPathExpansionDepth = 16,
            RootPathLargeFanoutThreshold = 200
        },
        _ => new RetentionOptions()
    };

    public static RetentionOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
