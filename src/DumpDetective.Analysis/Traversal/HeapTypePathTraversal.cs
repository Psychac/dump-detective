using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

internal static class HeapTypePathTraversal
{
    /// <summary>
    /// Forward BFS from <paramref name="startAddr"/>. Returns the distinct type names
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
}
