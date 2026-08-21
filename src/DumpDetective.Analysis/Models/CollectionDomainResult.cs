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
    IReadOnlyList<CollectionGenerationStats>? GenerationBreakdown = null
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
    ulong? RetainedBytes = null);
