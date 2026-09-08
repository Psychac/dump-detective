namespace DumpDetective.Core.Options;

public sealed class RetentionOptions
{
    /// <summary>
    /// Number of top highly-referenced objects/types to carry through the analyzer. Bounds real
    /// work, not just display rows: it sizes the in-scan top-K accumulator in
    /// <c>DominatorAnalyzer.BuildLeakSignalsFromReverseIndex</c> and determines how many
    /// candidates get an expensive per-item retained-size BFS walk and root-path evidence search
    /// in <c>Analyze</c>/<c>PopulateRetainedBytes</c>/<c>PopulateEvidence</c> — see §9.18
    /// implementation notes in docs/refactor/analysis-profile-removal-plan.md for why this stayed
    /// a Category-5 kept threshold rather than moving to the render layer.
    /// </summary>
    public int TopHighlyReferencedObjectsToShow { get; init; } = 15;

    public int HighReferenceThreshold { get; init; } = 50;

    /// <summary>
    /// Memory bound on the incoming-reference-count dictionary. Only applies to
    /// <c>DominatorAnalyzer.AnalyzeObjectsPass</c>'s live-heap fallback, used when no disk-backed
    /// reverse-edge index is available — the primary, reverse-index-backed path
    /// (<c>BuildLeakSignalsFromReverseIndex</c>) is exhaustive by construction and never applies
    /// this cap.
    /// </summary>
    public int MaxReferenceAddresses { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum number of objects subjected to full reference-field enumeration during
    /// the incoming-reference-count pass. Each traced object requires at least one
    /// <c>heap.GetObject()</c> call against the dump file, which is the primary
    /// bottleneck on large (multi-GB) dumps. Set to 0 to disable the limit (only safe on small
    /// dumps). Like <see cref="MaxReferenceAddresses"/>, only applies to
    /// <c>DominatorAnalyzer.AnalyzeObjectsPass</c>'s live-heap fallback path — the primary
    /// reverse-index-backed leak-signal pass is exhaustive, never capped. Also reused as the BFS
    /// breadth bound (<c>maxBreadth</c>) for the top-K retained-size walks in
    /// <c>Analyze</c>/<c>PopulateRetainedBytes</c>, a legitimate per-candidate safety bound
    /// distinct from its fallback-pass meaning above.
    /// </summary>
    public int MaxLeakScanObjects { get; init; } = 2_000_000;

    /// <summary>
    /// Maximum number of candidate nodes explored during the "highly referenced objects" root-path
    /// evidence search in <c>PopulateEvidence</c>. Kept alongside <see cref="RootPathLargeFanoutThreshold"/>
    /// under the same D3 reasoning (§9.18): this bounds a purely decorative evidence-path search —
    /// the analyzer's actual reported retained-byte totals come from the exact dominator tree,
    /// computed independently, so a truncated search only costs a confidence downgrade
    /// (<c>searchTruncated</c>), never a wrong number.
    /// </summary>
    public int MaxRootPathCandidateNodes { get; init; } = 5_000;

    /// <summary>Depth companion to <see cref="MaxRootPathCandidateNodes"/> — same D3 reasoning.</summary>
    public int MaxRootPathCandidateDepth { get; init; } = 8;

    /// <summary>Depth companion to <see cref="MaxRootPathCandidateNodes"/> — same D3 reasoning.</summary>
    public int MaxRootPathExpansionDepth { get; init; } = 12;

    /// <summary>
    /// Fanout threshold above which a reference path is considered "large" and skipped, to avoid
    /// exploring extremely high-connectivity clusters (static caches, singletons, interned
    /// strings) during the evidence-path search in <c>PopulateEvidence</c>. Resolved by D3: kept
    /// as-is — removing it risks a multi-million-node single-query blowup for a purely cosmetic
    /// payoff (see <see cref="MaxRootPathCandidateNodes"/>'s remarks).
    /// </summary>
    public int RootPathLargeFanoutThreshold { get; init; } = 100;

    /// <summary>
    /// §D9 (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md): enables the exact
    /// Lengauer-Tarjan dominator-tree computation. Default <c>true</c>: exact mode is attempted by
    /// default.
    ///
    /// <para>No memory-usage budget gates this (removed —
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §10.8's "review the
    /// budget" conclusion: the calibrated byte-cost model this used to enforce was fit to two dumps
    /// under a memory profile that predated Stage A and Stage B ever sharing a walk, and its abort
    /// path could leave Stage A's reverse-edge index silently incomplete). A reachable population too
    /// large to represent in this pipeline's <c>int</c>-indexed arrays throws instead of silently
    /// producing a wrong tree — <c>DominatorAnalyzer</c>'s exact-tree path catches that and falls
    /// back to the existing heuristic; <c>DiskBackedObjectIndexWriter.Build</c>'s Stage B path catches
    /// it and skips persistence with a warning, without losing anything else the index build already
    /// completed.</para>
    /// </summary>
    public bool EnableExactDominatorTree { get; init; } = true;

    /// <summary>
    /// Safety bound (not a display truncation) on how many immediate-dominator hops
    /// <c>DominatorAnalyzer</c> walks via <c>IDominatorTreeProvider.TryGetImmediateDominator</c>
    /// when building a per-type dominance chain for the Gen2/LOH sub-table. A real dominator
    /// tree's depth is bounded only by the longest single-parent chain in the heap — a linked
    /// list, queue, or continuation chain can make this arbitrarily deep (up to the reachable
    /// object count), and each hop costs one <c>heap.GetObject()</c> dump-file read, so this
    /// exists purely to guard against a pathological chain turning a decorative diagnostic into
    /// an unbounded-runtime walk. Set generously above any depth expected on a normal object
    /// graph; hitting it is itself a signal (surfaced as a trailing "chain continues" entry)
    /// rather than a silent truncation.
    /// </summary>
    public int MaxDominatorChainDepth { get; init; } = 64;

    /// <summary>
    /// Safety bound (not a display truncation) on how many object instances
    /// <c>DominatorAnalyzer</c>'s cross-type population overlap pass (§8b,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) collects while
    /// streaming the disk-backed heap index looking for instances of the Gen2/LOH sub-table's
    /// candidate types. Unlike the sample-based §8a check, §8b needs every instance of every
    /// candidate type to answer "how many objects of type A live inside type B's retained
    /// subgraph" exactly — for a candidate type with millions of instances (a large collection,
    /// or exactly the self-referential-chain shape this tool exists to catch) that's a real,
    /// data-dependent cost, not one this option silently trims away: hitting the cap surfaces as
    /// <c>DominatorDomainResult.CrossTypeOverlapInstanceScanCapped</c> rather than a quietly
    /// under-counted result.
    /// </summary>
    public int MaxCrossTypeOverlapInstancesScanned { get; init; } = 2_000_000;
}
