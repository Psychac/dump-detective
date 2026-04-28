using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

internal readonly record struct RootCandidate(string RootKind, ulong Address);

internal readonly struct RootPathSearchBudget
{
    public int MaxRoots { get; init; }
    public int MaxNodes { get; init; }
    public int MaxEdges { get; init; }
    public int MaxDepth { get; init; }
    public TimeSpan MaxDuration { get; init; }

    public RootPathSearchBudget(int maxRoots, int maxNodes, int maxEdges, int maxDepth, TimeSpan maxDuration)
    {
        MaxRoots = maxRoots;
        MaxNodes = maxNodes;
        MaxEdges = maxEdges;
        MaxDepth = maxDepth;
        MaxDuration = maxDuration;
    }
}

internal enum PathSearchCapReason
{
    None,
    RootLimit,
    NodeLimit,
    EdgeLimit,
    TimeLimit
}

internal sealed class RootPathSearchResult
{
    public bool Found { get; }
    public string? RootKind { get; }
    public IReadOnlyList<ulong>? Path { get; }
    public bool Capped { get; }
    public PathSearchCapReason CapReason { get; }
    public int RootsChecked { get; }
    public int NodesVisited { get; }
    public int EdgesVisited { get; }
    public TimeSpan Elapsed { get; }

    public RootPathSearchResult(
        bool found,
        string? rootKind,
        IReadOnlyList<ulong>? path,
        bool capped,
        PathSearchCapReason capReason,
        int rootsChecked,
        int nodesVisited,
        int edgesVisited,
        TimeSpan elapsed)
    {
        Found = found;
        RootKind = rootKind;
        Path = path;
        Capped = capped;
        CapReason = capReason;
        RootsChecked = rootsChecked;
        NodesVisited = nodesVisited;
        EdgesVisited = edgesVisited;
        Elapsed = elapsed;
    }
}

internal sealed class RootPathFinder(ClrHeap heap, ReferenceGraph graph)
{
    private readonly ClrHeap _heap = heap;
    private readonly ReferenceGraph _graph = graph;

    public RootPathSearchResult TryFindAnyRootPath(
        IReadOnlyList<RootCandidate> roots,
        ulong targetAddress,
        RootPathSearchBudget budget)
    {
        if (targetAddress == 0)
        {
            return new RootPathSearchResult(false, null, null, false, PathSearchCapReason.None, 0, 0, 0, TimeSpan.Zero);
        }

        long startTicks = Stopwatch.GetTimestamp();
        TimeSpan maxDuration = budget.MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : budget.MaxDuration;
        long maxTicks = (long)(maxDuration.TotalSeconds * Stopwatch.Frequency);
        int rootsChecked = 0;
        int nodesVisited = 0;
        int edgesVisited = 0;

        int maxRoots = Math.Max(1, budget.MaxRoots);
        int maxNodes = Math.Max(1, budget.MaxNodes);
        int maxEdges = Math.Max(1, budget.MaxEdges);
        int maxDepth = Math.Max(1, budget.MaxDepth);

        int rootLoop = Math.Min(roots.Count, maxRoots);

        // OPT-#1: Pre-allocate BFS collections once and Clear() between root iterations instead of
        // allocating new Queue/HashSet/Dictionary per root — mirrors the pattern in ReferenceChainAnalyzer.
        Queue<(ulong Address, int Depth)> queue = new(capacity: 256);
        HashSet<ulong> visited = new(capacity: 1024);
        Dictionary<ulong, ulong> previous = new(capacity: 1024);

        for (int rootIndex = 0; rootIndex < rootLoop; rootIndex++)
        {
            if (Stopwatch.GetTimestamp() - startTicks > maxTicks)
            {
                return Complete(false, null, null, true, PathSearchCapReason.TimeLimit, rootsChecked, nodesVisited, edgesVisited, startTicks);
            }

            RootCandidate root = roots[rootIndex];
            rootsChecked++;

            if (root.Address == targetAddress)
            {
                return Complete(true, root.RootKind, [targetAddress], false, PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, startTicks);
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
                if (Stopwatch.GetTimestamp() - startTicks > maxTicks)
                {
                    return Complete(false, null, null, true, PathSearchCapReason.TimeLimit, rootsChecked, nodesVisited, edgesVisited, startTicks);
                }

                (ulong current, int depth) = queue.Dequeue();
                nodesVisited++;

                if (nodesVisited > maxNodes)
                {
                    return Complete(false, null, null, true, PathSearchCapReason.NodeLimit, rootsChecked, nodesVisited, edgesVisited, startTicks);
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
                        return Complete(false, null, null, true, PathSearchCapReason.EdgeLimit, rootsChecked, nodesVisited, edgesVisited, startTicks);
                    }

                    if (referenceAddress == targetAddress)
                    {
                        IReadOnlyList<ulong> path = ReconstructPath(previous, root.Address, targetAddress, current);
                        return Complete(true, root.RootKind, path, false, PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, startTicks);
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
        return Complete(false, null, null, cappedByRoots, cappedByRoots ? PathSearchCapReason.RootLimit : PathSearchCapReason.None, rootsChecked, nodesVisited, edgesVisited, startTicks);
    }

    private static IReadOnlyList<ulong> ReconstructPath(Dictionary<ulong, ulong> previous, ulong startAddress, ulong targetAddress, ulong targetParent)
    {
        // OPT-#17 (PERF-MED-03): Stack naturally produces root→target order; eliminates the O(N)
        // List.Reverse() second pass that the previous List<ulong> approach required.
        Stack<ulong> stack = new(capacity: 16);
        stack.Push(targetAddress);
        ulong cursor = targetParent;

        while (cursor != startAddress && previous.TryGetValue(cursor, out ulong parent))
        {
            stack.Push(cursor);
            cursor = parent;
        }

        stack.Push(startAddress);
        return stack.ToArray();
    }

    private static RootPathSearchResult Complete(
        bool found,
        string? rootKind,
        IReadOnlyList<ulong>? path,
        bool capped,
        PathSearchCapReason capReason,
        int rootsChecked,
        int nodesVisited,
        int edgesVisited,
        long startTicks)
    {
        // OPT-#16 (PERF-HIGH-01): Stopwatch.GetElapsedTime avoids a kernel-mode transition;
        // available on .NET 7+ (including .NET 10).
        return new RootPathSearchResult(
            found,
            rootKind,
            path,
            capped,
            capReason,
            rootsChecked,
            nodesVisited,
            edgesVisited,
            Stopwatch.GetElapsedTime(startTicks));
    }
}
