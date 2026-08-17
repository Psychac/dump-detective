namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Core reachability walk + CSR build (design doc §D2, §D4, §D6) — deliberately heap-agnostic
/// (injected root addresses and a <c>successors</c> function), same pattern as
/// <see cref="BidirectionalGraphSearch"/>/<see cref="LengauerTarjan"/>, so it's unit-testable with
/// synthetic graphs and works unmodified whether <c>successors</c> is backed by the persisted
/// forward-edge index (§D5, preferred) or a live <c>ClrObject.EnumerateReferences</c> walk (§D4,
/// fallback) — see <see cref="ReachableGraphBuilder"/> for the ClrHeap-aware adapter that picks
/// which one to inject.
///
/// Single-pass edge capture + O(N+E) counting-sort CSR build (§D4 — the two-pass count-then-fill
/// design was measured to be slower, not faster, than this): walk each reachable node's successors
/// exactly once, capturing <c>(fromId, toId)</c> pairs into a flat buffer while incrementing degree
/// counters inline; build the final forward+reverse CSR arrays afterward via counting-sort
/// redistribution.
/// </summary>
internal static class ReachableGraphWalker
{
    public static ReachableGraphWalkResult Walk(
        IReadOnlyList<ulong> rootAddresses,
        Func<ulong, IEnumerable<ulong>> successors,
        int nodeCap,
        CancellationToken cancellationToken)
    {
        var idMap = new DenseIdMap();
        // ChunkedBuffer, not List<T> (§D4/§D6): at 25GB-dump scale (E ≈ 137M edges) a List<T>'s
        // double-and-copy growth would transiently hold up to ~2x the final size, with the old and
        // new backing arrays both alive during the copy — see the design doc's Measured Numbers.
        var addresses = new ChunkedBuffer<ulong>();
        var outDegree = new ChunkedBuffer<int>();
        var isRoot = new ChunkedBuffer<bool>();
        var edgeFrom = new ChunkedBuffer<int>();
        var edgeTo = new ChunkedBuffer<int>();
        var frontier = new Queue<int>();

        (int id, bool isNew) GetOrAddId(ulong addr)
        {
            if (idMap.TryGetValue(addr, out int existing))
                return (existing, false);

            int newId = addresses.Count;
            idMap.Add(addr, newId);
            addresses.Add(addr);
            outDegree.Add(0);
            isRoot.Add(false);
            return (newId, true);
        }

        foreach (ulong rootAddr in rootAddresses)
        {
            if (rootAddr == 0)
                continue;

            (int id, bool isNew) = GetOrAddId(rootAddr);
            isRoot[id] = true;
            if (isNew)
                frontier.Enqueue(id);
        }

        // §D6: cap enforced mid-walk (reachable count isn't known until the walk completes) — abort
        // and discard partial state the moment the frontier would exceed nodeCap.
        if (nodeCap > 0 && addresses.Count > nodeCap)
            return ReachableGraphWalkResult.Capped();

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int id = frontier.Dequeue();
            ulong address = addresses[id];

            foreach (ulong childAddr in successors(address))
            {
                if (childAddr == 0)
                    continue;

                (int childId, bool isNew) = GetOrAddId(childAddr);
                edgeFrom.Add(id);
                edgeTo.Add(childId);
                outDegree[id]++;

                if (isNew)
                {
                    if (nodeCap > 0 && addresses.Count > nodeCap)
                        return ReachableGraphWalkResult.Capped();

                    frontier.Enqueue(childId);
                }
            }
        }

        int nodeCount = addresses.Count;
        long edgeCount = edgeFrom.Count;

        // O(N+E) counting-sort CSR build — forward and reverse in the same pass, no dependency on
        // the disk-backed reverse index's fanout cap (§D3: this collection is uncapped by
        // construction, sourced from the same edge list either way).
        var inDegree = new int[nodeCount];
        for (int e = 0; e < edgeCount; e++)
            inDegree[edgeTo[e]]++;

        var fwdOffsets = new int[nodeCount + 1];
        var revOffsets = new int[nodeCount + 1];
        for (int i = 0; i < nodeCount; i++)
        {
            fwdOffsets[i + 1] = fwdOffsets[i] + outDegree[i];
            revOffsets[i + 1] = revOffsets[i] + inDegree[i];
        }

        var fwdTargets = new int[edgeCount];
        var revTargets = new int[edgeCount];
        var fwdCursor = (int[])fwdOffsets.Clone();
        var revCursor = (int[])revOffsets.Clone();
        for (int e = 0; e < edgeCount; e++)
        {
            int from = edgeFrom[e];
            int to = edgeTo[e];
            fwdTargets[fwdCursor[from]++] = to;
            revTargets[revCursor[to]++] = from;
        }

        return new ReachableGraphWalkResult(
            capExceeded: false,
            nodeCount: nodeCount,
            addresses: addresses.ToArray(),
            outDegree: outDegree.ToArray(),
            inDegree: inDegree,
            isRoot: isRoot.ToArray(),
            fwdOffsets: fwdOffsets,
            fwdTargets: fwdTargets,
            revOffsets: revOffsets,
            revTargets: revTargets);
    }
}

/// <summary>Result of <see cref="ReachableGraphWalker.Walk"/> — see §D6 for the capped case.</summary>
internal sealed class ReachableGraphWalkResult
{
    public bool CapExceeded { get; }
    public int NodeCount { get; }
    public ulong[] Addresses { get; } = Array.Empty<ulong>();
    public int[] OutDegree { get; } = Array.Empty<int>();
    public int[] InDegree { get; } = Array.Empty<int>();
    /// <summary>
    /// True for nodes seeded directly from <c>rootAddresses</c> — these are LT's virtual-root
    /// children and must never be excluded by <see cref="LeafFolder"/> regardless of degree, since
    /// they have an "invisible" incoming edge from the virtual root the CSR doesn't represent.
    /// </summary>
    public bool[] IsRoot { get; } = Array.Empty<bool>();
    public int[] FwdOffsets { get; } = Array.Empty<int>();
    public int[] FwdTargets { get; } = Array.Empty<int>();
    public int[] RevOffsets { get; } = Array.Empty<int>();
    public int[] RevTargets { get; } = Array.Empty<int>();

    public ReachableGraphWalkResult(
        bool capExceeded,
        int nodeCount,
        ulong[] addresses,
        int[] outDegree,
        int[] inDegree,
        bool[] isRoot,
        int[] fwdOffsets,
        int[] fwdTargets,
        int[] revOffsets,
        int[] revTargets)
    {
        CapExceeded = capExceeded;
        NodeCount = nodeCount;
        Addresses = addresses;
        OutDegree = outDegree;
        InDegree = inDegree;
        IsRoot = isRoot;
        FwdOffsets = fwdOffsets;
        FwdTargets = fwdTargets;
        RevOffsets = revOffsets;
        RevTargets = revTargets;
    }

    private ReachableGraphWalkResult(bool capExceeded)
    {
        CapExceeded = capExceeded;
    }

    public static ReachableGraphWalkResult Capped() => new(capExceeded: true);
}
