using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Single bounded forward-BFS primitive for the heap object graph. Enforces the
/// project's 20-depth cap internally regardless of caller-requested depth, replacing
/// the separate, inconsistently-capped implementations that used to live in
/// <c>HeapTypePathTraversal</c>, <c>BoundedRetainedSizeBfs</c>, and
/// <c>HeapAnalysisCache.GetRetainedObjects</c>.
/// </summary>
internal static class BoundedGraphWalk
{
    private const int AbsoluteMaxDepth = 20;

    /// <summary>
    /// Forward BFS from <paramref name="startAddr"/>. Returns distinct type names
    /// encountered in BFS order (excluding the start object itself), bounded by
    /// <paramref name="maxNodes"/> and <paramref name="maxDepth"/>.
    /// </summary>
    public static IReadOnlyList<string> CollectForwardTypeNames(
        ClrHeap heap,
        ulong startAddr,
        int maxNodes,
        int maxDepth,
        out bool wasCapped)
    {
        wasCapped = false;
        if (startAddr == 0)
            return [];

        maxDepth = Math.Min(maxDepth, AbsoluteMaxDepth);

        var visited = new HashSet<ulong>(capacity: 64) { startAddr };
        var queue = new Queue<(ulong Addr, int Depth)>(capacity: 64);
        var typeNames = new List<string>(capacity: 16);

        queue.Enqueue((startAddr, 0));
        int nodesVisited = 0;

        while (queue.Count > 0)
        {
            (ulong addr, int depth) = queue.Dequeue();
            nodesVisited++;

            if (nodesVisited > maxNodes || depth >= maxDepth)
            {
                wasCapped = true;
                break;
            }

            ClrObject obj = heap.GetObject(addr);
            if (!obj.IsValid || obj.Type is null)
                continue;

            if (depth > 0 && obj.Type.Name is string name)
            {
                if (typeNames.Count == 0 || typeNames[typeNames.Count - 1] != name)
                    typeNames.Add(name);
            }

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (child.IsValid && child.Address != 0 && visited.Add(child.Address))
                    queue.Enqueue((child.Address, depth + 1));
            }
        }

        return typeNames;
    }

    /// <summary>
    /// Sums the size of every object reachable from <paramref name="root"/> that is not
    /// already present in <paramref name="visited"/>, then adds every newly-discovered
    /// address to <paramref name="visited"/>. Callers control exclusive-vs-overlapping
    /// retained-size semantics via the lifetime of the <paramref name="visited"/> set
    /// they pass in (shared across a batch, or fresh per call).
    /// </summary>
    public static ulong ComputeExclusiveRetained(
        ClrObject root,
        ClrHeap heap,
        HashSet<ulong> visited,
        int maxBreadth = 10_000,
        int maxDepth = 20)
    {
        if (!root.IsValid || root.Address == 0 || root.Type is null)
            return 0;

        if (visited.Contains(root.Address))
            return 0;

        maxBreadth = Math.Max(1, maxBreadth);
        maxDepth = Math.Min(Math.Max(1, maxDepth), AbsoluteMaxDepth);

        Queue<(ulong Address, int Depth)> queue = new(capacity: 256);
        HashSet<ulong> discovered = new(capacity: 256) { root.Address };
        queue.Enqueue((root.Address, 0));

        ulong totalSize = 0;
        int nodesSeen = 0;

        while (queue.Count > 0)
        {
            (ulong address, int depth) = queue.Dequeue();
            nodesSeen++;

            if (nodesSeen > maxBreadth)
                break;

            if (visited.Contains(address))
                continue;

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null)
                continue;

            totalSize += obj.Size;

            if (depth >= maxDepth)
                continue;

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                if (visited.Contains(child.Address))
                    continue;

                if (discovered.Add(child.Address))
                    queue.Enqueue((child.Address, depth + 1));
            }
        }

        foreach (ulong address in discovered)
            visited.Add(address);

        return totalSize;
    }

    /// <summary>
    /// Forward BFS from <paramref name="rootAddress"/>, collecting every reachable
    /// address up to <paramref name="maxObjects"/>, bounded to depth 20 by default
    /// (CLAUDE.md's mandated BFS-depth cap). Replaces
    /// <c>HeapAnalysisCache.GetRetainedObjects</c>, which had no depth cap at all.
    /// Sets <paramref name="wasCapped"/> to true if the scan hit <paramref name="maxObjects"/> limit.
    /// </summary>
    public static HashSet<ulong> CollectRetainedObjects(
        ClrHeap heap,
        ulong rootAddress,
        out bool wasCapped,
        int maxObjects = 10_000,
        int maxDepth = AbsoluteMaxDepth)
    {
        wasCapped = false;
        maxDepth = Math.Min(maxDepth, AbsoluteMaxDepth);

        var retained = new HashSet<ulong>(capacity: Math.Min(1000, maxObjects));
        var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);

        queue.Enqueue((rootAddress, 0));
        retained.Add(rootAddress);

        while (queue.Count > 0 && retained.Count < maxObjects)
        {
            (ulong current, int depth) = queue.Dequeue();
            ClrObject obj = heap.GetObject(current);

            if (!obj.IsValid)
                continue;

            if (depth >= maxDepth)
                continue;

            foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
            {
                if (reference.IsValid && retained.Add(reference.Address))
                {
                    if (retained.Count >= maxObjects)
                    {
                        wasCapped = true;
                        break;
                    }
                    queue.Enqueue((reference.Address, depth + 1));
                }
            }
        }

        if (retained.Count >= maxObjects)
            wasCapped = true;

        return retained;
    }
}
