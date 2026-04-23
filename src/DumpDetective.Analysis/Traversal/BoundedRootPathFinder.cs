using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

internal readonly record struct RootCandidate(string RootKind, ulong Address);

internal readonly record struct BoundedPathSearchBudget(
    int MaxRoots,
    int MaxNodes,
    int MaxEdges,
    int MaxDepth,
    TimeSpan MaxDuration);

internal enum PathSearchCapReason
{
    None,
    RootLimit,
    NodeLimit,
    EdgeLimit,
    TimeLimit
}

internal sealed record BoundedPathSearchResult(
    bool Found,
    string? RootKind,
    IReadOnlyList<ulong>? Path,
    bool Capped,
    PathSearchCapReason CapReason,
    int RootsChecked,
    int NodesVisited,
    int EdgesVisited,
    TimeSpan Elapsed);

internal sealed class BoundedRootPathFinder(ClrHeap heap, LazyReferenceGraph graph)
{
    private readonly ClrHeap _heap = heap;
    private readonly LazyReferenceGraph _graph = graph;

    public BoundedPathSearchResult TryFindAnyRootPath(
        IReadOnlyList<RootCandidate> roots,
        ulong targetAddress,
        BoundedPathSearchBudget budget)
    {
        if (targetAddress == 0)
        {
            return new BoundedPathSearchResult(false, null, null, false, PathSearchCapReason.None, 0, 0, 0, TimeSpan.Zero);
        }

        DateTime start = DateTime.UtcNow;
        int rootsChecked = 0;
        int nodesVisited = 0;
        int edgesVisited = 0;

        int maxRoots = Math.Max(1, budget.MaxRoots);
        int maxNodes = Math.Max(1, budget.MaxNodes);
        int maxEdges = Math.Max(1, budget.MaxEdges);
        int maxDepth = Math.Max(1, budget.MaxDepth);
        TimeSpan maxDuration = budget.MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : budget.MaxDuration;

        int rootLoop = Math.Min(roots.Count, maxRoots);

        // OPT-#1: Pre-allocate BFS collections once and Clear() between root iterations instead of
        // allocating new Queue/HashSet/Dictionary per root — mirrors the pattern in ReferenceChainAnalyzer.
        Queue<(ulong Address, int Depth)> queue = new(capacity: 256);
        HashSet<ulong> visited = new(capacity: 1024);
        Dictionary<ulong, ulong> previous = new(capacity: 1024);

        for (int rootIndex = 0; rootIndex < rootLoop; rootIndex++)
        {
            if (DateTime.UtcNow - start > maxDuration)
            {
                return Complete(false, null, null, true, PathSearchCapReason.TimeLimit, rootsChecked, nodesVisited, edgesVisited, start);
            }

            RootCandidate root = roots[rootIndex];
            rootsChecked++;

            if (root.Address == targetAddress)
            {
                return Complete(true, root.RootKind, [targetAddress], false, PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, start);
            }

            if (root.Address == 0)
            {
                continue;
            }

            visited.Clear();
            visited.Add(root.Address);
            previous.Clear();
            queue.Clear();
            queue.Enqueue((root.Address, 0));

            while (queue.Count > 0)
            {
                if (DateTime.UtcNow - start > maxDuration)
                {
                    return Complete(false, null, null, true, PathSearchCapReason.TimeLimit, rootsChecked, nodesVisited, edgesVisited, start);
                }

                (ulong current, int depth) = queue.Dequeue();
                nodesVisited++;

                if (nodesVisited > maxNodes)
                {
                    return Complete(false, null, null, true, PathSearchCapReason.NodeLimit, rootsChecked, nodesVisited, edgesVisited, start);
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (ulong referenceAddress in _graph.GetReferences(current))
                {
                    edgesVisited++;
                    if (edgesVisited > maxEdges)
                    {
                        return Complete(false, null, null, true, PathSearchCapReason.EdgeLimit, rootsChecked, nodesVisited, edgesVisited, start);
                    }

                    if (referenceAddress == targetAddress)
                    {
                        IReadOnlyList<ulong> path = ReconstructPath(previous, root.Address, targetAddress, current);
                        return Complete(true, root.RootKind, path, false, PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, start);
                    }

                    if (visited.Add(referenceAddress))
                    {
                        previous[referenceAddress] = current;
                        queue.Enqueue((referenceAddress, depth + 1));
                    }
                }
            }
        }

        bool cappedByRoots = roots.Count > maxRoots;
        return Complete(false, null, null, cappedByRoots, cappedByRoots ? PathSearchCapReason.RootLimit : PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, start);
    }

    private static IReadOnlyList<ulong> ReconstructPath(Dictionary<ulong, ulong> previous, ulong startAddress, ulong targetAddress, ulong targetParent)
    {
        List<ulong> reversed = new(capacity: 16) { targetAddress, targetParent };
        ulong cursor = targetParent;

        while (cursor != startAddress && previous.TryGetValue(cursor, out ulong parent))
        {
            reversed.Add(parent);
            cursor = parent;
        }

        reversed.Reverse();
        return reversed;
    }

    private static BoundedPathSearchResult Complete(
        bool found,
        string? rootKind,
        IReadOnlyList<ulong>? path,
        bool capped,
        PathSearchCapReason capReason,
        int rootsChecked,
        int nodesVisited,
        int edgesVisited,
        DateTime started)
    {
        return new BoundedPathSearchResult(
            found,
            rootKind,
            path,
            capped,
            capReason,
            rootsChecked,
            nodesVisited,
            edgesVisited,
            DateTime.UtcNow - started);
    }
}
