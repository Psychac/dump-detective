using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Caches MethodTable -> <see cref="ClrType"/> resolutions for the duration
/// of an analysis session. Uses the heap index sample addresses as a fast-path
/// when available to avoid repeated ClrMD MethodTable lookups.
/// </summary>
internal class MethodTableCache
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;
    private readonly Dictionary<ulong, ClrType?> _typeCache = new Dictionary<ulong, ClrType?>(capacity: 512);

    public MethodTableCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    public int CachedTypeCount => _typeCache.Count;

    /// <summary>
    /// Resolves a <see cref="ClrType"/> for the given <paramref name="methodTable"/>
    /// using the provided <paramref name="heap"/>. Returns null if the type cannot be resolved.
    /// Results are cached so each MethodTable is resolved at most once.
    /// </summary>
    public ClrType? GetTypeByMethodTable(ClrHeap heap, ulong methodTable)
    {
        if (methodTable == 0)
            return null;

        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_typeCache.TryGetValue(methodTable, out var cached))
            return cached;

        // Fast-path: if we have an index with a sample address, try to materialize
        // the object at the sample and read its Type without doing a MethodTable lookup.
        var built = _getHeapIndex();
        if (built?.TypeAggregates is IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates
            && aggregates.TryGetValue(methodTable, out var aggregate)
            && aggregate.SampleAddress != 0)
        {
            try
            {
                var sample = heap.GetObject(aggregate.SampleAddress);
                if (sample.IsValid && sample.Type is not null)
                {
                    _typeCache[methodTable] = sample.Type;
                    return sample.Type;
                }
            }
            catch
            {
                // ignore and fall through to MethodTable lookup
            }
        }

        // Fallback: ask ClrHeap to resolve the MethodTable.
        try
        {
            var type = heap.GetTypeByMethodTable(methodTable);
            _typeCache[methodTable] = type; // may be null sentinel
            _lastLookupTime = DateTime.UtcNow;
            return type;
        }
        catch
        {
            _typeCache[methodTable] = null;
            return null;
        }
    }

    private DateTime? _lastLookupTime;

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(MethodTableCache),
            LastBuildDurationMs = null,
            LastBuildStatus = "success",
            EntryCount = _typeCache.Count,
            MemoryUsageBytes = 0,
            LastBuildTime = _lastLookupTime,
            IsHealthy = true
        };
    }
}
