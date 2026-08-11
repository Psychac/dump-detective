namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Pure meet-in-the-middle BFS over an address graph: a multi-source forward frontier from every
/// root and a backward frontier from the target expand one level at a time until they intersect.
/// Deliberately heap-agnostic (neighbor lookup is injected) so this — the actual novel algorithm —
/// is unit-testable with synthetic graphs, independent of <c>ClrHeap</c>/<c>ClrObject</c>, which
/// this repo has no fixtures to construct in tests. <see cref="IndexBackedBidirectionalSearch"/>
/// is the ClrMD-aware adapter that supplies the neighbor functions.
/// </summary>
internal static class BidirectionalGraphSearch
{
    public static bool TryFindPath(
        ulong target,
        IReadOnlyList<(string RootKind, ulong Address)> roots,
        Func<ulong, IEnumerable<ulong>> getForwardNeighbors,
        Func<ulong, IEnumerable<ulong>> getBackwardNeighbors,
        int maxNodes,
        int maxTotalDepth,
        out string? rootKind,
        out List<ulong>? path,
        out int candidateSetSize,
        out bool budgetExhausted,
        CancellationToken cancellationToken = default)
    {
        rootKind = null;
        path = null;
        candidateSetSize = 0;
        budgetExhausted = false;

        var rootKindByAddress = new Dictionary<ulong, string>();
        foreach ((string kind, ulong addr) in roots)
        {
            if (addr == 0)
                continue;

            rootKindByAddress.TryAdd(addr, kind);

            if (addr == target)
            {
                rootKind = kind;
                path = new List<ulong> { target };
                return true;
            }
        }

        if (rootKindByAddress.Count == 0)
            return false;

        var forwardVisited = new HashSet<ulong>(rootKindByAddress.Keys);
        var forwardPrev = new Dictionary<ulong, ulong>();
        List<ulong> forwardFrontier = new(rootKindByAddress.Keys);

        var backwardVisited = new HashSet<ulong> { target };
        var backwardNext = new Dictionary<ulong, ulong>();
        List<ulong> backwardFrontier = new() { target };

        maxTotalDepth = Math.Max(1, maxTotalDepth);

        ulong? meetingNode = null;
        int forwardDepth = 0;
        int backwardDepth = 0;

        while (meetingNode is null
               && (forwardFrontier.Count > 0 || backwardFrontier.Count > 0)
               && forwardVisited.Count + backwardVisited.Count < maxNodes
               && forwardDepth + backwardDepth < maxTotalDepth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (forwardFrontier.Count > 0)
            {
                forwardFrontier = ExpandLevel(forwardFrontier, forwardVisited, forwardPrev, backwardVisited, getForwardNeighbors, ref meetingNode);
                forwardDepth++;
            }
            else
            {
                forwardFrontier = [];
            }

            if (meetingNode is null && backwardFrontier.Count > 0)
            {
                backwardFrontier = ExpandLevel(backwardFrontier, backwardVisited, backwardNext, forwardVisited, getBackwardNeighbors, ref meetingNode);
                backwardDepth++;
            }
            else if (meetingNode is not null)
            {
                backwardFrontier = [];
            }
        }

        candidateSetSize = forwardVisited.Count + backwardVisited.Count;

        if (meetingNode is null)
        {
            budgetExhausted = forwardVisited.Count + backwardVisited.Count >= maxNodes
                || forwardDepth + backwardDepth >= maxTotalDepth;
            return false;
        }

        ulong meet = meetingNode.Value;

        // Forward half: walk predecessors from the meeting node back to whichever root reached
        // it, collecting in reverse, then flip to root→...→meet order.
        var forwardHalf = new List<ulong> { meet };
        ulong cursor = meet;
        while (forwardPrev.TryGetValue(cursor, out ulong prev))
        {
            forwardHalf.Add(prev);
            cursor = prev;
        }
        forwardHalf.Reverse();
        rootKind = rootKindByAddress.TryGetValue(cursor, out string? kindFound) ? kindFound : null;

        // Backward half: successors already trace meet→...→target in the right order.
        var backwardHalf = new List<ulong>();
        cursor = meet;
        while (backwardNext.TryGetValue(cursor, out ulong next))
        {
            backwardHalf.Add(next);
            cursor = next;
        }

        var fullPath = new List<ulong>(forwardHalf.Count + backwardHalf.Count);
        fullPath.AddRange(forwardHalf);
        fullPath.AddRange(backwardHalf);
        path = fullPath;
        return true;
    }

    /// <summary>Expands one BFS level for either direction — identical shape for forward and backward, only the neighbor function and bookkeeping dictionaries differ.</summary>
    private static List<ulong> ExpandLevel(
        List<ulong> frontier,
        HashSet<ulong> visited,
        Dictionary<ulong, ulong> predecessor,
        HashSet<ulong> otherVisited,
        Func<ulong, IEnumerable<ulong>> getNeighbors,
        ref ulong? meetingNode)
    {
        var next = new List<ulong>();

        foreach (ulong node in frontier)
        {
            foreach (ulong neighbor in getNeighbors(node))
            {
                if (neighbor == 0 || !visited.Add(neighbor))
                    continue;

                predecessor[neighbor] = node;

                if (otherVisited.Contains(neighbor))
                {
                    meetingNode = neighbor;
                    return next;
                }

                next.Add(neighbor);
            }
        }

        return next;
    }
}
