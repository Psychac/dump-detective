using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

internal sealed class LazyReferenceGraph(ClrHeap heap)
{
    private readonly ClrHeap _heap = heap;
    private readonly Dictionary<ulong, ulong[]> _cache = new(capacity: 2048);

    // OPT-#7: Bound the edge cache to prevent unbounded growth on dense graphs across
    // all top-type BFS runs. When exceeded, clear and continue — bounding peak memory
    // without hurting common cases where the cache stays well under the limit.
    private const int MaxCachedNodes = 500_000;

    public IReadOnlyList<ulong> GetReferences(ulong address)
    {
        if (_cache.TryGetValue(address, out ulong[]? cached))
        {
            return cached;
        }

        if (_cache.Count >= MaxCachedNodes)
            _cache.Clear();

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid)
        {
            _cache[address] = [];
            return _cache[address];
        }

        List<ulong> refs = new(capacity: 8);
        foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
        {
            if (!reference.IsValid)
            {
                continue;
            }

            refs.Add(reference.Address);
        }

        ulong[] result = refs.Count == 0 ? [] : refs.ToArray();
        _cache[address] = result;
        return result;
    }
}
