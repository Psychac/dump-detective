using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Cache;

internal class TypeMetadataCache
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;
    private readonly MethodTableCache? _methodTableCache;
    private readonly Dictionary<ulong, bool> _methodTableHasRefs = new Dictionary<ulong, bool>(capacity: 512);

    public TypeMetadataCache(Func<HeapIndexBuildResult?> getHeapIndex, MethodTableCache? methodTableCache = null)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
        _methodTableCache = methodTableCache;
    }

    public bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable)
    {
        if (methodTable == 0)
            return false;

        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_methodTableHasRefs.TryGetValue(methodTable, out var cached))
            return cached;

        // Fast path: if we have a prebuilt index, hydrate from the index sample address.
        var built = _getHeapIndex();
        if (built?.TypeAggregates is IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates
            && aggregates.TryGetValue(methodTable, out var aggregate))
        {
            if (aggregate.SampleAddress != 0)
            {
                try
                {
                    ClrObject sample = heap.GetObject(aggregate.SampleAddress);
                    bool has = sample.IsValid && sample.Type is not null && sample.Type.ContainsPointers;
                    _methodTableHasRefs[methodTable] = has;
                    return has;
                }
                catch
                {
                    // fallthrough to conservative default below
                }
            }
        }

        // Fallback: resolve the ClrType via MethodTableCache when available, otherwise ask ClrHeap.
        try
        {
            ClrType? type = _methodTableCache?.GetTypeByMethodTable(heap, methodTable) ?? heap.GetTypeByMethodTable(methodTable);
            if (type is not null)
            {
                bool has = false;
                if (type.IsArray)
                {
                    has = type.ComponentType?.IsObjectReference == true;
                }
                else
                {
                    foreach (ClrInstanceField field in type.Fields)
                    {
                        if (field.IsObjectReference)
                        {
                            has = true;
                            break;
                        }
                    }
                }

                _methodTableHasRefs[methodTable] = has;
                return has;
            }
        }
        catch
        {
            // ignore and fall through to conservative default
        }

        // Conservative default: assume method-table has outgoing refs to avoid missing referents.
        _methodTableHasRefs[methodTable] = true;
        return true;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(TypeMetadataCache),
            LastBuildDurationMs = null,
            LastBuildStatus = "success",
            EntryCount = _methodTableHasRefs.Count,
            MemoryUsageBytes = 0,
            LastBuildTime = null,
            IsHealthy = true
        };
    }
}
