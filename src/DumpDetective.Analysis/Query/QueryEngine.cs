using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Query;

/// <summary>
/// Structured querying layer. Operates exclusively on pre-built indices stored in
/// <see cref="IHeapAnalysisCache"/> — never enumerates the live <see cref="ClrHeap"/>.
/// All methods use <c>yield return</c> streaming; no results are materialized internally.
/// </summary>
internal sealed class QueryEngine : IQueryEngine
{
    private readonly IHeapAnalysisCache _cache;
    private readonly ClrHeap _heap;

    public QueryEngine(IHeapAnalysisCache cache, ClrHeap heap)
    {
        _cache = cache;
        _heap = heap;
    }

    /// <inheritdoc/>
    public IEnumerable<TypeSnapshot> TopTypesBySize(int topN)
    {
        if (topN <= 0)
            yield break;

        Dictionary<string, CachedTypeStatistics> stats = _cache.GetOrBuildTypeStatistics(_heap);

        // Avoid LINQ OrderByDescending allocation on hot path: use explicit sort on a pooled span.
        // For the typical scenario (topN ≤ 20, stats ≤ 50k entries) this is fast enough.
        // We intentionally avoid ToList() on the full dictionary — instead we sort in place.
        var entries = new CachedTypeStatistics[stats.Count];
        int i = 0;
        foreach (CachedTypeStatistics s in stats.Values)
            entries[i++] = s;

        // Partial sort: only need topN items — but Array.Sort on the full set is simpler and
        // the dictionary is a one-time result already built; allocation is bounded by stats.Count.
        Array.Sort(entries, 0, i, SizeDescendingComparer.Instance);

        int limit = Math.Min(topN, i);
        for (int j = 0; j < limit; j++)
        {
            CachedTypeStatistics s = entries[j];
            yield return new TypeSnapshot(s.TypeName, s.Count, s.TotalSize, s.LohSize);
        }
    }

    /// <inheritdoc/>
    public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> ObjectsOfType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            yield break;

        // Verify the type exists in the already-built statistics cache — O(1) lookup.
        Dictionary<string, CachedTypeStatistics> stats = _cache.GetOrBuildTypeStatistics(_heap);
        if (!stats.TryGetValue(typeName, out _))
            yield break;

        // Stream the index, resolving each unique MethodTable exactly once via ClrMD metadata
        // (not heap object enumeration). Matches are yielded immediately — no intermediate list.
        var mtDecision = new Dictionary<ulong, bool>();

        foreach ((ulong address, ulong mt, ulong size) in _cache.EnumerateIndexedEntriesAsTuples())
        {
            if (!mtDecision.TryGetValue(mt, out bool isMatch))
            {
                isMatch = _heap.GetTypeByMethodTable(mt)?.Name == typeName;
                mtDecision[mt] = isMatch;
            }

            if (isMatch)
                yield return (address, mt, size);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class SizeDescendingComparer : IComparer<CachedTypeStatistics>
    {
        public static readonly SizeDescendingComparer Instance = new();
        public int Compare(CachedTypeStatistics? x, CachedTypeStatistics? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            return y.TotalSize.CompareTo(x.TotalSize);
        }
    }
}
