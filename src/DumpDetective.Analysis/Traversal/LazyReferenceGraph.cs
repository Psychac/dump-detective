using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal;

internal sealed class LazyReferenceGraph(ClrHeap heap) : IReferenceProvider
{
    private readonly ClrHeap _heap = heap;
    private readonly Dictionary<ulong, ulong[]> _cache = new(capacity: 2048);

    // Explicit implementation: adapts IReadOnlyList<ulong> return to IEnumerable<ulong> for interface compatibility.
    IEnumerable<ulong> IReferenceProvider.GetReferences(ulong address) => GetReferences(address);
    // OPT-#7: Bound the edge cache to prevent unbounded growth on dense graphs across
    // all top-type BFS runs. When exceeded, clear and continue — bounding peak memory
    // without hurting common cases where the cache stays well under the limit.
    //
    // Full-clear eviction strategy (deliberate):
    //   When the 500 000-node limit is hit, the entire cache is discarded and rebuilt
    //   from the next BFS traversal. This is intentionally aggressive — it caps peak
    //   memory at the cost of potential re-fetching in dense-graph scenarios (e.g. a
    //   large collection type appearing in every top-N sample). The 500 000 limit is
    //   sized to avoid this thrash in practice for typical production dumps
    //   (<500 MB managed heap). If thrash is observed on very large dumps, consider:
    //     • halving the limit (reduces window before thrash occurs, but also peak memory)
    //     • partial eviction: evict the oldest 50% by insertion order using
    //       _cache.Keys.Take(toRemove).ToList() before _cache.Remove(key)
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
