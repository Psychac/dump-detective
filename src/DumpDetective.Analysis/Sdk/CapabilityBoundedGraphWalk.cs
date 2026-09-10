using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// The capability-scoped counterpart to <c>Traversal.BoundedGraphWalk</c> (this project's single
/// canonical <c>ClrHeap</c>-based forward-BFS helper, see docs/architecture.md § 7) — same bounded
/// forward BFS, ported against <see cref="IHeapObjectLookup"/>/<see cref="IHeapReferenceQuery"/>
/// instead of a raw <c>ClrHeap</c>. First shared by <c>GCRootAnalyzer</c> and <c>MemoryAnalyzer</c>'s
/// retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — extracted once a
/// second real consumer existed, not speculatively ahead of one.
/// </summary>
internal static class CapabilityBoundedGraphWalk
{
    /// <summary>
    /// Sums the size of every object reachable from <paramref name="rootAddress"/> that is not
    /// already present in <paramref name="visited"/>, then adds every newly-discovered address to
    /// <paramref name="visited"/>. Callers control exclusive-vs-overlapping retained-size semantics
    /// via the lifetime of the <paramref name="visited"/> set they pass in (shared across a batch,
    /// or fresh per call).
    /// </summary>
    public static ulong ComputeExclusiveRetained(
        ulong rootAddress,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery referenceQuery,
        HashSet<ulong> visited,
        int maxBreadth,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        if (visited.Contains(rootAddress))
            return 0;

        var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);
        var discovered = new HashSet<ulong>(capacity: 256) { rootAddress };
        queue.Enqueue((rootAddress, 0));

        ulong totalSize = 0;
        int nodesSeen = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (ulong address, int depth) = queue.Dequeue();
            nodesSeen++;

            if (nodesSeen > maxBreadth)
                break;

            if (visited.Contains(address))
                continue;

            if (!objectLookup.TryGetObject(address, out HeapObjectRef obj))
                continue;

            totalSize += obj.Size;

            if (depth >= maxDepth)
                continue;

            foreach (ulong childAddress in referenceQuery.EnumerateReferences(address))
            {
                if (childAddress == 0 || visited.Contains(childAddress))
                    continue;

                if (discovered.Add(childAddress))
                    queue.Enqueue((childAddress, depth + 1));
            }
        }

        foreach (ulong address in discovered)
            visited.Add(address);

        return totalSize;
    }

    /// <summary>
    /// Forward BFS from <paramref name="startAddr"/>. Returns distinct type display names
    /// encountered in BFS order (excluding the start object itself), bounded by
    /// <paramref name="maxNodes"/> and <paramref name="maxDepth"/>.
    /// </summary>
    public static List<string> CollectForwardTypeNames(
        ulong startAddr,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery referenceQuery,
        int maxNodes,
        int maxDepth,
        out bool wasCapped,
        CancellationToken cancellationToken)
    {
        wasCapped = false;
        if (startAddr == 0)
            return [];

        var visited = new HashSet<ulong>(capacity: 64) { startAddr };
        var queue = new Queue<(ulong Addr, int Depth)>(capacity: 64);
        var typeNames = new List<string>(capacity: 16);

        queue.Enqueue((startAddr, 0));
        int nodesVisited = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (ulong addr, int depth) = queue.Dequeue();
            nodesVisited++;

            if (nodesVisited > maxNodes || depth >= maxDepth)
            {
                wasCapped = true;
                break;
            }

            if (!objectLookup.TryGetObject(addr, out HeapObjectRef obj))
                continue;

            if (depth > 0)
            {
                string name = obj.TypeDisplayName;
                if (typeNames.Count == 0 || typeNames[^1] != name)
                    typeNames.Add(name);
            }

            foreach (ulong childAddr in referenceQuery.EnumerateReferences(addr))
            {
                if (childAddr != 0 && visited.Add(childAddr))
                    queue.Enqueue((childAddr, depth + 1));
            }
        }

        return typeNames;
    }
}
