using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Collections

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

internal sealed record CollectionDomainResult(
    int TotalCollections,
    int Dictionaries,
    int Lists,
    int ArrayLists,
    int Stacks,
    int SortedLists,
    int SortedSets,
    int HashSets,
    int Queues,
    ulong TotalWastedMemory,
    int WastefulCollectionCount,
    IReadOnlyList<WastefulCollectionSnapshot>? TopWastefulCollections = null,
    IReadOnlyDictionary<CollectionKind, int>? WasteCountsByKind = null,
    IReadOnlyDictionary<CollectionKind, ulong>? WasteBytesByKind = null,
    IReadOnlyList<CollectionGenerationStats>? GenerationBreakdown = null,
    int ImmutableArrays = 0,
    int ImmutableArrayBuilders = 0,
    /// <summary>Wasteful-collection count grouped by element type (e.g. "System.String"),
    /// computed from the exact scan-time accumulators — not derived from the capped top-N list,
    /// which would undercount every element type (see the P2-1 fix for the same bug on
    /// <see cref="WasteCountsByKind"/>).</summary>
    IReadOnlyDictionary<string, int>? WasteCountsByElementType = null,
    /// <summary>Wasted bytes grouped by element type, same accumulation source as
    /// <see cref="WasteCountsByElementType"/>.</summary>
    IReadOnlyDictionary<string, ulong>? WasteBytesByElementType = null
) : AnalyzerDomainResult;
internal sealed record WastefulCollectionSnapshot(
    string Type,
    CollectionKind Kind,
    int Count,
    int Capacity,
    double FillRate,
    ulong WastedMemory,
    ulong Address,
    int? Head = null,
    int? Tail = null,
    ulong? LargestContiguousFreeSegmentBytes = null,
    int? FreeSegmentCount = null,
    ulong ElementSize = 0,
    string ElementType = "",
    string SizeEstimateConfidence = "Unknown",
    string DetectionMethod = "",
    string? RootDescription = null,
    /// <summary>Exact dominator-tree retained bytes for this collection's own address (§9,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — what freeing it
    /// would actually free, distinct from <see cref="WastedMemory"/>'s over-capacity estimate.
    /// Null when the exact tree wasn't available.</summary>
    ulong? RetainedBytes = null,
    /// <summary>Actionable fix for this specific collection's over-capacity (e.g. "Call
    /// TrimExcess()", "Construct with initial capacity ~N"). Always a concrete capacity fix —
    /// reachability is never asserted here since <see cref="RootDescription"/> only reflects a
    /// budget-limited search, not proof the collection is actually unreachable.</summary>
    string Recommendation = "",
    /// <summary>Immediate-parent hint from one reverse-index lookup (P3-4) — cheaper than
    /// <see cref="RootDescription"/>'s full BFS and populated independently of it. Null when no
    /// reverse index was available or the object has no recorded parents. When the object has
    /// more than one recorded parent (or the index truncated its parent list), this reports the
    /// ambiguity explicitly (e.g. "3 referrers, e.g. CacheManager") rather than picking one
    /// arbitrary parent and presenting it as the definitive owner.</summary>
    string? OwnerTypeHint = null);
