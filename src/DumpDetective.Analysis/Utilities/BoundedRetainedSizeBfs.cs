using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Utilities;

internal static class BoundedRetainedSizeBfs
{
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
        maxDepth = Math.Max(1, maxDepth);

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
}