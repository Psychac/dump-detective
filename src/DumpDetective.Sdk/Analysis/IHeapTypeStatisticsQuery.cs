using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>Aggregate per-type facts — mirrors <c>IHeapAnalysisCache.GetOrBuildTypeStatistics</c>'s
/// <c>CachedTypeStatistics</c> shape, source-neutral, extended 2026-09-10 with the per-generation
/// fields <c>GCGenerationAnalyzer</c> (the Phase 1 retyping pilot) measurably needs — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. These mirror the dump-side
/// <c>TypeAggregateIndexEntry</c> one-for-one; <see cref="Gen0Count"/>/<see cref="Gen1Count"/>/
/// <see cref="Gen2Count"/>/<see cref="Gen2TotalSize"/>/<see cref="IsFinalizableType"/> are all zero/
/// false when <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/> is <c>false</c> — the
/// dump-side cache's coarser fallback statistics carry no per-generation breakdown at all.</summary>
public readonly record struct HeapTypeStatistics(
    TypeRef Type,
    long InstanceCount,
    ulong TotalSize,
    long LohCount,
    ulong LohSize,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    ulong Gen2TotalSize,
    bool IsFinalizableType);

/// <summary>The <c>heap.types</c> capability's aggregate-statistics surface (distinct
/// <see cref="IHeapObjectStream"/>/<see cref="IHeapObjectLookup"/>, per-object).</summary>
public interface IHeapTypeStatisticsQuery
{
    IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics();

    /// <summary>
    /// <c>true</c> when <see cref="GetTypeStatistics"/> is backed by the dump's prebuilt per-type
    /// generation index (real <see cref="HeapTypeStatistics.Gen0Count"/>/<see cref="HeapTypeStatistics.Gen1Count"/>/
    /// <see cref="HeapTypeStatistics.Gen2Count"/>/<see cref="HeapTypeStatistics.Gen2TotalSize"/>/
    /// <see cref="HeapTypeStatistics.IsFinalizableType"/> data); <c>false</c> when it falls back to
    /// coarser statistics with no generation breakdown (every one of those fields reads as its
    /// default). Mirrors the dump-side <c>HeapAnalysisCache.TryGetHeapIndex</c>-vs-
    /// <c>GetOrBuildTypeStatistics</c> fork — callers that care about generation fidelity (rather
    /// than just count/size) should check this before trusting the generation fields.
    /// </summary>
    bool HasExactGenerationData { get; }

    /// <summary>
    /// Exact per-generation byte totals for the small object heap (excludes LOH/POH), computed
    /// directly from segment sub-ranges — always available regardless of
    /// <see cref="HasExactGenerationData"/>, since it needs no per-type index at all.
    /// </summary>
    (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetDistinctMethodTables</c> — type
    /// least one live instance, without full object-index scan. <c>null</c> unavailable.</summary>
    IReadOnlyList<TypeRef>? TryGetDistinctTypes();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetGlobalSizeBuckets</c> — method
    /// raw <c>long[]?</c>, but every other SDK collection type `IReadOnly*`,
    /// `IReadOnlyList` instead reproducing encapsulation leak; see
    /// docs/refactor/modularity/phase-1-sdk-review-findings.md item 6. <c>null</c>
    /// unavailable.</summary>
    IReadOnlyList<long>? TryGetGlobalSizeBuckets();
}
