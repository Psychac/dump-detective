namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Pure meet-in-the-middle BFS over an address graph: a multi-source forward frontier from every
/// root and a backward frontier from the target expand one level at a time until they intersect.
/// Deliberately heap-agnostic (neighbor lookup is injected) so this — the actual novel algorithm —
/// is unit-testable with synthetic graphs, independent of <c>ClrHeap</c>/<c>ClrObject</c>, which
/// this repo has no fixtures to construct in tests. <see cref="IndexBackedBidirectionalSearch"/>
/// is the ClrMD-aware adapter that supplies the neighbor functions.
///
/// Instance (not static): callers that invoke <see cref="TryFindPath"/> repeatedly against
/// different targets (one search per candidate object) are expected to reuse a single instance so
/// the frontier/visited/predecessor collections below are cleared and reused instead of allocated
/// fresh per search.
/// </summary>
internal sealed class BidirectionalGraphSearch
{
    private readonly Dictionary<ulong, string> _rootKindByAddress = new();
    private readonly HashSet<ulong> _forwardVisited = new();
    private readonly Dictionary<ulong, ulong> _forwardPrev = new();
    private List<ulong> _forwardFrontier = new();
    private List<ulong> _forwardFrontierNext = new();

    private readonly HashSet<ulong> _backwardVisited = new();
    private readonly Dictionary<ulong, ulong> _backwardNext = new();
    private List<ulong> _backwardFrontier = new();
    private List<ulong> _backwardFrontierNext = new();

    public bool TryFindPath(
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

        _rootKindByAddress.Clear();
        _forwardVisited.Clear();
        _forwardPrev.Clear();
        _forwardFrontier.Clear();
        _forwardFrontierNext.Clear();
        _backwardVisited.Clear();
        _backwardNext.Clear();
        _backwardFrontier.Clear();
        _backwardFrontierNext.Clear();

        foreach ((string kind, ulong addr) in roots)
        {
            if (addr == 0)
                continue;

            _rootKindByAddress.TryAdd(addr, kind);

            if (addr == target)
            {
                rootKind = kind;
                path = new List<ulong> { target };
                return true;
            }
        }

        if (_rootKindByAddress.Count == 0)
            return false;

        foreach (ulong addr in _rootKindByAddress.Keys)
        {
            _forwardVisited.Add(addr);
            _forwardFrontier.Add(addr);
        }

        _backwardVisited.Add(target);
        _backwardFrontier.Add(target);

        maxTotalDepth = Math.Max(1, maxTotalDepth);

        ulong? meetingNode = null;
        int forwardDepth = 0;
        int backwardDepth = 0;

        while (meetingNode is null
               && (_forwardFrontier.Count > 0 || _backwardFrontier.Count > 0)
               && _forwardVisited.Count + _backwardVisited.Count < maxNodes
               && forwardDepth + backwardDepth < maxTotalDepth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_forwardFrontier.Count > 0)
            {
                ExpandLevel(_forwardFrontier, _forwardFrontierNext, _forwardVisited, _forwardPrev, _backwardVisited, getForwardNeighbors, ref meetingNode);
                (_forwardFrontier, _forwardFrontierNext) = (_forwardFrontierNext, _forwardFrontier);
                forwardDepth++;
            }
            else
            {
                _forwardFrontier.Clear();
            }

            if (meetingNode is null && _backwardFrontier.Count > 0)
            {
                ExpandLevel(_backwardFrontier, _backwardFrontierNext, _backwardVisited, _backwardNext, _forwardVisited, getBackwardNeighbors, ref meetingNode);
                (_backwardFrontier, _backwardFrontierNext) = (_backwardFrontierNext, _backwardFrontier);
                backwardDepth++;
            }
            else if (meetingNode is not null)
            {
                _backwardFrontier.Clear();
            }
        }

        candidateSetSize = _forwardVisited.Count + _backwardVisited.Count;

        if (meetingNode is null)
        {
            budgetExhausted = _forwardVisited.Count + _backwardVisited.Count >= maxNodes
                || forwardDepth + backwardDepth >= maxTotalDepth;
            return false;
        }

        ulong meet = meetingNode.Value;

        // Forward half: walk predecessors from the meeting node back to whichever root reached
        // it, collecting in reverse, then flip to root→...→meet order.
        var forwardHalf = new List<ulong> { meet };
        ulong cursor = meet;
        while (_forwardPrev.TryGetValue(cursor, out ulong prev))
        {
            forwardHalf.Add(prev);
            cursor = prev;
        }
        forwardHalf.Reverse();
        rootKind = _rootKindByAddress.TryGetValue(cursor, out string? kindFound) ? kindFound : null;

        // Backward half: successors already trace meet→...→target in the right order.
        var backwardHalf = new List<ulong>();
        cursor = meet;
        while (_backwardNext.TryGetValue(cursor, out ulong next))
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

    /// <summary>
    /// Expands one BFS level for either direction — identical shape for forward and backward,
    /// only the neighbor function and bookkeeping dictionaries differ. Writes into
    /// <paramref name="next"/> (cleared first) rather than allocating, so the caller can ping-pong
    /// between two pooled frontier buffers instead of allocating a new list per level.
    /// </summary>
    private static void ExpandLevel(
        List<ulong> frontier,
        List<ulong> next,
        HashSet<ulong> visited,
        Dictionary<ulong, ulong> predecessor,
        HashSet<ulong> otherVisited,
        Func<ulong, IEnumerable<ulong>> getNeighbors,
        ref ulong? meetingNode)
    {
        next.Clear();

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
                    return;
                }

                next.Add(neighbor);
            }
        }
    }
}
