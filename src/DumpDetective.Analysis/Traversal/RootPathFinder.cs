using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Bounded limits governing a root-path search. Decoupled from any single analyzer's options type
/// so the search can be reused by future "why is this object alive" consumers.
/// </summary>
internal readonly struct RootPathSearchLimits
{
    public int MaxCandidateNodes { get; init; }
    public int MaxCandidateDepth { get; init; }
    public int MaxRootExpansionDepth { get; init; }
    public int LargeFanoutThreshold { get; init; }
}

internal interface IPathSearchTelemetry
{
    void IncrementPruned();
    void IncrementLargeFanout();
}

/// <summary>
/// Finds a path from any GC root to a target object, constrained to a bounded candidate set.
/// Reusable by any analyzer that needs "why is this object alive" traversal.
/// </summary>
internal sealed class RootPathFinder
{
    private readonly ClrHeap _heap;
    private readonly IReferenceProvider _provider;
    private readonly IBackwardReferenceProvider? _reverseIndexProvider;
    private readonly RootPathSearchLimits _limits;
    private readonly IPathSearchTelemetry _telemetry;
    private readonly Func<ClrType?, bool> _isNoise;
    private readonly Func<ClrType?, bool> _forceExpand;
    private readonly IHeapAnalysisCache? _cache;

    public RootPathFinder(
        ClrHeap heap,
        IReferenceProvider provider,
        RootPathSearchLimits limits,
        IPathSearchTelemetry telemetry,
        Func<ClrType?, bool> isNoise,
        Func<ClrType?, bool> forceExpand,
        IBackwardReferenceProvider? reverseIndexProvider = null,
        IHeapAnalysisCache? cache = null)
    {
        _heap = heap;
        _provider = provider;
        _reverseIndexProvider = reverseIndexProvider;
        _limits = limits;
        _telemetry = telemetry;
        _isNoise = isNoise;
        _forceExpand = forceExpand;
        _cache = cache;
    }

    public bool TryFindAnyRootPath(
        ulong target,
        IReadOnlyList<(string RootKind, ulong Address)> roots,
        out string? rootKind,
        out List<ulong>? path,
        out bool searchTruncated,
        out int candidateSetSize,
        out int reverseIndexEntryCount,
        CancellationToken cancellationToken = default)
    {
        rootKind = null;
        path = null;
        searchTruncated = false;

        // When a disk-backed reverse index is available, use genuine bidirectional BFS instead of
        // the forward-only heuristic below — see IndexBackedBidirectionalSearch for why this
        // finds paths the heuristic can miss and covers a smaller search space.
        if (_reverseIndexProvider is not null)
        {
            var indexBackedSearch = new IndexBackedBidirectionalSearch(
                _heap, _provider, _reverseIndexProvider, _limits, _telemetry, _isNoise, _forceExpand, _cache);

            return indexBackedSearch.TryFindPath(
                target, roots, out rootKind, out path, out searchTruncated,
                out candidateSetSize, out reverseIndexEntryCount, cancellationToken);
        }

        // Phase 1: build candidate set via bidirectional expansion.
        var candidateBuilder = new CandidateSetBuilder(_heap, _provider, _limits, _telemetry, _isNoise, _forceExpand, _cache);
        HashSet<ulong> candidateSet = candidateBuilder.Build(target, roots);
        candidateSetSize = candidateSet.Count;

        // Phase 2: build scoped reverse index over candidate set only.
        var reverseIndex = new ReverseReferenceIndex();
        reverseIndex.Build(candidateSet, _heap, _provider, _limits, _telemetry, _forceExpand, _cache);
        reverseIndexEntryCount = reverseIndex.EntryCount;

        // Phase 3: constrained BFS from roots, staying inside candidate set.
        var pathFinder = new BidirectionalPathFinder(_heap, _provider, candidateSet, reverseIndex, _limits, _telemetry, _forceExpand, _cache);

        var scanCounter = new ObjectScanCounter("Root path scan", reportEveryObjects: 500, reportEveryElapsed: TimeSpan.FromSeconds(2));
        foreach ((string kind, ulong rootAddress) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!candidateSet.Contains(rootAddress) && rootAddress != target)
                continue;

            if (pathFinder.TryFindPath(rootAddress, target, out List<ulong>? addresses, out bool limited))
            {
                scanCounter.Complete();
                rootKind = kind;
                path = addresses;
                return true;
            }

            if (limited)
                searchTruncated = true;
        }

        scanCounter.Complete();
        return false;
    }
}

// ── ReverseReferenceIndex ─────────────────────────────────────────────────
/// <summary>
/// Builds a parent-lookup map scoped to a candidate set only.
/// Never indexes the full heap.
/// </summary>
internal sealed class ReverseReferenceIndex
{
    private readonly Dictionary<ulong, List<ulong>> _map = new();

    public int EntryCount => _map.Count;

    /// <summary>
    /// For every node in <paramref name="candidateSet"/>, enumerate its forward
    /// references via <paramref name="provider"/> and record an edge child→parent
    /// only when the child is also inside the candidate set.
    /// </summary>
    public void Build(
        HashSet<ulong> candidateSet,
        ClrHeap heap,
        IReferenceProvider provider,
        RootPathSearchLimits limits,
        IPathSearchTelemetry telemetry,
        Func<ClrType?, bool> forceExpand,
        IHeapAnalysisCache? cache = null)
    {
        foreach (ulong obj in candidateSet)
        {
            // OPT (docs/cache/cache-architecture.md Phase 6): type-classification gate
            // only — provider.GetReferences below does the actual traversal.
            ClrType? type = RootPathSearchSupport.ResolveType(heap, cache, obj);
            if (type is null)
                continue;

            int counted = 0;
            bool expand = forceExpand(type);

            foreach (var childAddr in provider.GetReferences(obj))
            {
                counted++;
                if (!expand && counted > limits.LargeFanoutThreshold)
                {
                    telemetry.IncrementLargeFanout();
                    break;
                }

                if (childAddr == 0)
                    continue;

                if (!candidateSet.Contains(childAddr))
                    continue;

                if (!_map.TryGetValue(childAddr, out var list))
                {
                    list = new List<ulong>();
                    _map[childAddr] = list;
                }

                list.Add(obj);
            }
        }
    }

    public IEnumerable<ulong> GetParents(ulong obj)
        => _map.TryGetValue(obj, out var list) ? list : Enumerable.Empty<ulong>();
}

// ── CandidateSetBuilder ───────────────────────────────────────────────────
/// <summary>
/// Builds a bounded candidate set via true bidirectional expansion:
/// forward from roots (limited depth) and forward from target (simulating reverse via
/// the heap walk), meeting in the middle.
/// </summary>
internal sealed class CandidateSetBuilder
{
    private readonly ClrHeap _heap;
    private readonly IReferenceProvider _provider;
    private readonly RootPathSearchLimits _limits;
    private readonly IPathSearchTelemetry _telemetry;
    private readonly Func<ClrType?, bool> _isNoise;
    private readonly Func<ClrType?, bool> _forceExpand;
    private readonly IHeapAnalysisCache? _cache;

    public CandidateSetBuilder(
        ClrHeap heap,
        IReferenceProvider provider,
        RootPathSearchLimits limits,
        IPathSearchTelemetry telemetry,
        Func<ClrType?, bool> isNoise,
        Func<ClrType?, bool> forceExpand,
        IHeapAnalysisCache? cache = null)
    {
        _heap = heap;
        _provider = provider;
        _limits = limits;
        _telemetry = telemetry;
        _isNoise = isNoise;
        _forceExpand = forceExpand;
        _cache = cache;
    }

    public HashSet<ulong> Build(
        ulong target,
        IReadOnlyList<(string RootKind, ulong Address)> roots)
    {
        int maxNodes = _limits.MaxCandidateNodes;
        int maxDepth = _limits.MaxCandidateDepth;

        var candidate = new HashSet<ulong> { target };

        // Root frontier: expand forward from roots up to maxDepth levels.
        var rootQueue = new Queue<(ulong Address, int Depth)>();
        var rootVisited = new HashSet<ulong>();
        foreach ((_, ulong addr) in roots)
        {
            if (addr == 0) continue;
            if (rootVisited.Add(addr))
            {
                rootQueue.Enqueue((addr, 0));
                candidate.Add(addr);
            }
        }

        // Target frontier: expand forward from target (forward refs of target
        // are not useful for reverse; instead we do a second BFS from target
        // outward to find objects target references — useful to collect
        // the "neighbourhood" that is likely on a retaining chain).
        var targetQueue = new Queue<(ulong Address, int Depth)>();
        var targetVisited = new HashSet<ulong> { target };
        targetQueue.Enqueue((target, 0));

        // Interleave both frontiers until they share a node or limits are hit.
        while ((rootQueue.Count > 0 || targetQueue.Count > 0) && candidate.Count < maxNodes)
        {
            // Expand one step from root frontier.
            if (rootQueue.Count > 0)
            {
                (ulong cur, int depth) = rootQueue.Dequeue();
                if (depth < maxDepth)
                    ExpandForward(cur, depth, rootVisited, rootQueue, candidate, maxNodes);
            }

            // Expand one step from target frontier.
            if (targetQueue.Count > 0 && candidate.Count < maxNodes)
            {
                (ulong cur, int depth) = targetQueue.Dequeue();
                if (depth < maxDepth)
                    ExpandForward(cur, depth, targetVisited, targetQueue, candidate, maxNodes);
            }
        }

        return candidate;
    }

    private void ExpandForward(
        ulong address,
        int depth,
        HashSet<ulong> visited,
        Queue<(ulong, int)> queue,
        HashSet<ulong> candidate,
        int maxNodes)
    {
        // OPT (docs/cache/cache-architecture.md Phase 6): type-classification gate
        // only — _provider.GetReferences below does the actual traversal.
        ClrType? type = RootPathSearchSupport.ResolveType(_heap, _cache, address);
        if (type is null)
            return;

        if (_isNoise(type))
        {
            _telemetry.IncrementPruned();
            return;
        }

        int counted = 0;
        bool forceExpand = _forceExpand(type);

        foreach (var childAddr in _provider.GetReferences(address))
        {
            counted++;
            if (!forceExpand && counted > _limits.LargeFanoutThreshold)
            {
                _telemetry.IncrementLargeFanout();
                break;
            }

            if (childAddr == 0)
                continue;

            candidate.Add(childAddr);
            if (candidate.Count >= maxNodes)
                return;

            if (visited.Add(childAddr))
                queue.Enqueue((childAddr, depth + 1));
        }
    }
}

// ── BidirectionalPathFinder ───────────────────────────────────────────────
/// <summary>
/// BFS from a single root constrained to the candidate set.
/// Uses the reverse index only for path reconstruction (backpointers),
/// NOT for forward traversal — keeping the search purely forward-constrained.
/// </summary>
internal sealed class BidirectionalPathFinder
{
    private readonly ClrHeap _heap;
    private readonly IReferenceProvider _provider;
    private readonly HashSet<ulong> _candidateSet;
    private readonly ReverseReferenceIndex _reverseIndex;
    private readonly RootPathSearchLimits _limits;
    private readonly IPathSearchTelemetry _telemetry;
    private readonly Func<ClrType?, bool> _forceExpand;
    private readonly IHeapAnalysisCache? _cache;

    public BidirectionalPathFinder(
        ClrHeap heap,
        IReferenceProvider provider,
        HashSet<ulong> candidateSet,
        ReverseReferenceIndex reverseIndex,
        RootPathSearchLimits limits,
        IPathSearchTelemetry telemetry,
        Func<ClrType?, bool> forceExpand,
        IHeapAnalysisCache? cache = null)
    {
        _heap = heap;
        _provider = provider;
        _candidateSet = candidateSet;
        _reverseIndex = reverseIndex;
        _limits = limits;
        _telemetry = telemetry;
        _forceExpand = forceExpand;
        _cache = cache;
    }

    private readonly HashSet<ulong> _visited = new(capacity: 256);
    private readonly Dictionary<ulong, ulong> _previous = new(capacity: 256);
    private readonly Queue<(ulong Address, int Depth)> _queue = new(capacity: 128);

    public bool TryFindPath(
        ulong start,
        ulong target,
        out List<ulong>? path,
        out bool searchLimitReached)
    {
        path = null;
        searchLimitReached = false;

        if (start == target)
        {
            path = new List<ulong> { start };
            return true;
        }

        int maxDepth = _limits.MaxRootExpansionDepth;

        _visited.Clear();
        _previous.Clear();
        _queue.Clear();

        _visited.Add(start);
        _queue.Enqueue((start, 0));

        int searched = 0;
        int maxSearch = _limits.MaxCandidateNodes;

        while (_queue.Count > 0 && searched++ < maxSearch)
        {
            (ulong current, int depth) = _queue.Dequeue();

            if (depth >= maxDepth)
                continue;

            // OPT (docs/cache/cache-architecture.md Phase 6): type-classification gate
            // only — _provider.GetReferences below does the actual traversal.
            ClrType? type = RootPathSearchSupport.ResolveType(_heap, _cache, current);
            if (type is null)
                continue;

            int counted = 0;
            bool forceExpand = _forceExpand(type);

            foreach (var childAddr in _provider.GetReferences(current))
            {
                counted++;
                if (!forceExpand && counted > _limits.LargeFanoutThreshold)
                {
                    _telemetry.IncrementLargeFanout();
                    break;
                }

                if (childAddr == 0)
                    continue;

                // Constrain to candidate set.
                if (!_candidateSet.Contains(childAddr))
                    continue;

                if (childAddr == target)
                {
                    _previous[childAddr] = current;
                    path = ReconstructPath(start, target);
                    return true;
                }

                if (_visited.Add(childAddr))
                {
                    _previous[childAddr] = current;
                    _queue.Enqueue((childAddr, depth + 1));
                }
            }
        }

        searchLimitReached = _queue.Count > 0 && searched >= maxSearch;
        return false;
    }

    private List<ulong> ReconstructPath(ulong start, ulong target)
    {
        var reversed = new List<ulong>(capacity: 16) { target };
        ulong cursor = target;
        while (cursor != start && _previous.TryGetValue(cursor, out ulong parent))
        {
            reversed.Add(parent);
            cursor = parent;
        }
        reversed.Reverse();
        return reversed;
    }
}
