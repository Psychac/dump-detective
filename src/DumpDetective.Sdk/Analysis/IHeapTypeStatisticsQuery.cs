using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>Aggregate per-type facts — mirrors <c>IHeapAnalysisCache.GetOrBuildTypeStatistics</c>'s
/// <c>CachedTypeStatistics</c> shape, source-neutral.</summary>
public readonly record struct HeapTypeStatistics(TypeRef Type, long InstanceCount, ulong TotalSize);

/// <summary>The <c>heap.types</c> capability's aggregate-statistics surface (distinct from
/// <see cref="IHeapObjectStream"/>/<see cref="IHeapObjectLookup"/>, which are per-object).</summary>
public interface IHeapTypeStatisticsQuery
{
    IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetDistinctMethodTables</c> — every type with at
    /// least one live instance, without a full object-index scan. <c>null</c> when unavailable.</summary>
    IReadOnlyList<TypeRef>? TryGetDistinctTypes();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetGlobalSizeBuckets</c>. <c>null</c> when unavailable.</summary>
    long[]? TryGetGlobalSizeBuckets();
}
