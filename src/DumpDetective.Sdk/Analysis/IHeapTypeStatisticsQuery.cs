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
    bool IsFinalizableType,
    /// <summary>
    /// A representative live instance's address for this type, or 0 when none is known. Added
    /// 2026-09-11 for <c>MemoryAnalyzer</c>'s retyping — mirrors
    /// <c>IHeapAnalysisCache.GetSampleInstanceAddress</c>/<c>TypeAggregateIndexEntry.SampleAddress</c>,
    /// carried alongside the rest of this type's stats rather than as a separate by-name lookup
    /// method (which would either repeat this interface's per-type dictionary scan per call, or
    /// duplicate one internally) — a caller who already has a <see cref="HeapTypeStatistics"/> in
    /// hand never needs a second round trip.
    /// </summary>
    ulong SampleAddress = 0,
    /// <summary>
    /// The declaring module's file name, or an empty string when unresolved — mirrors
    /// <c>CachedTypeStatistics.ModuleName</c>. Deliberately not <c>string?</c>: the dump-side cache's
    /// own field is never null, only sometimes empty, and this preserves that exactly rather than
    /// introducing a normalization this interface doesn't need to make (callers that want `null` for
    /// "unresolved" — e.g. a report field — normalize at that point, same as the pre-retyping analyzer
    /// already did).
    /// </summary>
    string ModuleName = "");

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

    /// <summary>
    /// Exact total live object count from the Phase 1 scan, or <c>null</c> when
    /// <see cref="HasExactGenerationData"/> is <c>false</c> (no index). Added 2026-09-11 for
    /// <c>HeapTopologyAnalyzer</c>'s retyping — distinct from summing
    /// <see cref="HeapTypeStatistics.InstanceCount"/> across <see cref="GetTypeStatistics"/>, which
    /// can undercount: that dictionary is keyed by resolved type <em>name</em>, so two distinct
    /// method tables resolving to the same name collapse to one entry (documented, accepted gap on
    /// the dump-side implementation); this property is not derived from that dictionary.
    /// </summary>
    long? ExactObjectCount { get; }

    /// <summary>
    /// Sum of <see cref="HeapTypeStatistics.TotalSize"/> across every indexed type, or 0 when
    /// <see cref="HasExactGenerationData"/> is <c>false</c>. Added 2026-09-11 for
    /// <c>HeapTopologyAnalyzer</c>'s retyping — computed directly from the index on the dump-side
    /// implementation, not by summing <see cref="GetTypeStatistics"/>'s result, for the same
    /// name-collision reason as <see cref="ExactObjectCount"/>.
    /// </summary>
    ulong GetTotalIndexedBytes();

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
