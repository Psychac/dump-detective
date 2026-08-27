using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

internal sealed record DominatorDomainResult(
    int CandidateCount,
    int AnalyzedCount,
    ulong TotalEstimatedRetainedBytes,
    IReadOnlyList<TypeSnapshot> TopDominatorTypes,
    int MaxBreadth = 0,
    int MaxDepth = 20,
    int HighlyReferencedObjectCount = 0,
    /// <summary>
    /// Number of times the fallback incoming-reference-count pass's
    /// <c>SpaceSavingCounter</c> had to approximate — evict the current globally
    /// lowest-count tracked address and re-admit a newly seen address starting from that
    /// evicted count, once its capacity (<see cref="DumpDetective.Core.Options.RetentionOptions.MaxReferenceAddresses"/>)
    /// was reached. Approximated addresses still have a bounded-error count (never an
    /// under-count), unlike a plain fixed-capacity dictionary that would have dropped them
    /// entirely. Always 0 on the primary reverse-index-backed path, which is exhaustive.
    /// </summary>
    long ApproximatedReferenceAddresses = 0,
    IReadOnlyList<HighlyReferencedObjectSnapshot>? TopHighlyReferencedObjects = null,
    /// <summary>
    /// True when the incoming-reference-count pass stopped early because the number of
    /// objects traced reached <see cref="DumpDetective.Core.Options.RetentionOptions.MaxLeakScanObjects"/>.
    /// Highly-referenced-object results are partial when set.
    /// </summary>
    bool ObjectScanCapped = false,
    /// <summary>
    /// True when a disk-backed index was in use and the incoming-reference-count scan was
    /// skipped entirely to avoid O(N x fields) random reads against the dump file.
    /// Highly-referenced-object detection is unavailable in this mode; use the in-memory index mode
    /// on this dump if you need it (requires enough machine RAM).
    /// </summary>
    bool ReferenceCountingSkipped = false,
    /// <summary>
    /// Aggregated retention hotspots derived from the top highly-referenced objects.
    /// Provides type-level insight (count, bytes, incoming refs) beyond per-object rows.
    /// </summary>
    IReadOnlyList<RetentionTypeSnapshot>? TopRetentionTypes = null,
    /// <summary>
    /// Total shallow bytes represented by <see cref="TopHighlyReferencedObjects"/>.
    /// This scoped footprint metric is a headline for reporting/trending.
    /// </summary>
    ulong TopHighlyReferencedTotalBytes = 0,
    /// <summary>
    /// Maximum number of items to display in section builder tables (from RetentionOptions.TopHighlyReferencedObjectsToShow).
    /// Used to respect the configured display limit instead of hardcoding table size.
    /// </summary>
    int MaxTopDominatorTypesToShow = 15,
    /// <summary>
    /// Total heap size in bytes (sum of all objects on all heaps).
    /// Used to calculate retention pressure ratio (retained / total).
    /// Zero if heap was too large to enumerate entirely.
    /// </summary>
    ulong TotalHeapBytes = 0,
    /// <summary>
    /// Distribution of incoming-reference counts across all scanned objects, bucketed into
    /// ranges (e.g. "0 – 10", "200+"). Lets engineers see whether retention is driven by a few
    /// extreme hubs vs. a broad population of moderately-referenced objects.
    /// </summary>
    IReadOnlyList<FanInBucket>? FanInHistogram = null,
    /// <summary>
    /// §Report integration (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md
    /// §Architecture "Output model"): exact retained bytes per type name, from the Lengauer-Tarjan
    /// dominator tree, keyed by <see cref="TypeSnapshot.TypeName"/>. Null when the exact path wasn't
    /// attempted (§D9 <c>EnableExactDominatorTree</c> off), exceeded its node cap (§D6), or threw —
    /// in every one of those cases the report falls back to <see cref="TypeSnapshot.EstimatedRetainedBytes"/>
    /// unchanged. Only covers the type names already present in <see cref="TopDominatorTypes"/> (the
    /// report never displays more than that), not every reachable type.
    /// </summary>
    IReadOnlyDictionary<string, ulong>? ExactRetainedBytesByTypeName = null,
    /// <summary>
    /// Per-type dominance chain (P3-3): for each type name already present in
    /// <see cref="TopDominatorTypes"/>, the ancestor chain from the topmost dominator down to
    /// the type's sample object, walked via <c>IDominatorTreeProvider.TryGetImmediateDominator</c>
    /// starting at that type's sample address. Ordered root-most first, sample leaf last. Null
    /// (or missing a given type name) exactly when <see cref="ExactRetainedBytesByTypeName"/> is
    /// — same "exact tree unavailable this run" fallback. Bounded by
    /// <see cref="DumpDetective.Core.Options.RetentionOptions.MaxDominatorChainDepth"/>; a chain
    /// that hits that cap ends with a synthetic "chain continues" hop (<c>Address == 0</c>)
    /// rather than silently looking complete.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<DominatorChainHop>>? DominatorChainsByTypeName = null) : AnalyzerDomainResult;

internal sealed record HighlyReferencedObjectSnapshot(ulong Address, string TypeName, ulong Size, int IncomingReferences, ulong EstimatedRetainedBytes = 0, Evidence? Evidence = null);

internal sealed record RetentionTypeSnapshot(
    string TypeName,
    int ObjectCount,
    ulong TotalBytes,
    long TotalIncomingReferences,
    int MaxIncomingReferences,
    ulong EstimatedRetainedBytes = 0);

/// <summary>One hop in a dominance chain (P3-3) — see <see cref="DominatorDomainResult.DominatorChainsByTypeName"/>.</summary>
internal sealed record DominatorChainHop(string TypeName, ulong Address, ulong RetainedBytes);
