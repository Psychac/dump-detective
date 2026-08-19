using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Successor lookup for <see cref="ReachableGraphWalker"/>: writes <paramref name="address"/>'s child
/// addresses into <paramref name="buffer"/>, growing it if the node has more children than it
/// currently holds, and returns how many were written.
///
/// A plain delegate rather than a <c>Func&lt;ulong, IEnumerable&lt;ulong&gt;&gt;</c> so it can take the
/// buffer by <c>ref</c> — same reasoning as <see cref="LengauerTarjan"/>'s <c>NeighborsFunc</c>. The
/// walk calls this once per reachable node (millions of times) and copies the children into its CSR
/// immediately, so returning a fresh collection per node was pure garbage: ~235MB on a 3.3GB dump.
/// One buffer now serves the entire walk. See
/// docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 7.
/// </summary>
internal delegate int SuccessorsFunc(ulong address, ref ulong[] buffer);

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
        SuccessorsFunc successors,
        ExactDominatorTreeBudget budget,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        // Frontier BFS is the dominant cost on real dumps (millions of nodes) — without a tick
        // here the console progress line sits frozen on whatever phase preceded this call for the
        // whole walk, which reads as "stuck" even though the walk is actively making progress.
        var scanCounter = new ObjectScanCounter("computing exact dominator tree (tracing heap graph)", progress);
        // Dictionary<ulong,int>, not DenseIdMap (§7.3 item 2's 25GB verification round,
        // docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §8): DenseIdMap's
        // custom 13-bytes/slot open-addressed table was designed to save memory over Dictionary's
        // ~28-32 bytes/entry, but measured 2.6x slower wall-clock at 25GB scale (240,985 ms vs. a
        // HashSet-based walker's 91,528 ms for the same node/edge count) despite no peak-memory win
        // in either the 3.3GB or 25GB measurement — the memory-saving premise never paid off, and
        // Dictionary's more mature implementation (and this walker's own smaller footprint gap for
        // the same reason) is both safer and faster. See §7.3 item 2's full history before touching
        // this again — that confound (this measurement partly overlapped a period of genuine system
        // memory pressure on the test machine) is documented there, not fully resolved.
        var idMap = new Dictionary<ulong, int>();
        // ChunkedBuffer, not List<T> (§D4/§D6): at 25GB-dump scale (E ≈ 137M edges) a List<T>'s
        // double-and-copy growth would transiently hold up to ~2x the final size, with the old and
        // new backing arrays both alive during the copy — see the design doc's Measured Numbers.
        var addresses = new ChunkedBuffer<ulong>();
        var outDegree = new ChunkedBuffer<int>();
        var isRoot = new ChunkedBuffer<bool>();
        var edgeFrom = new ChunkedBuffer<int>();
        var edgeTo = new ChunkedBuffer<int>();
        var frontier = new Queue<int>();
        // One buffer for the whole walk — see SuccessorsFunc. Grows to the largest out-degree seen.
        var childBuffer = new ulong[64];

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

        // §D6: budget enforced mid-walk (neither the reachable count nor the edge count is known until
        // the walk completes) — abort and discard partial state the moment the projected peak would
        // exceed it. Both terms matter: a dense graph can blow the budget on edges while its node count
        // still looks comfortable, which the old node-only cap could not see.
        if (budget.IsExceededBy(addresses.Count, edgeFrom.Count))
            return ReachableGraphWalkResult.Capped();

        // Edges accumulate between node discoveries, so a purely discovery-driven check can overshoot
        // across a high-out-degree tail. Re-check every few million edges to bound that overshoot
        // without paying two multiplies on every single edge in the hot loop.
        const int EdgeCheckInterval = 4_000_000;
        long nextEdgeCheck = EdgeCheckInterval;

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int id = frontier.Dequeue();
            ulong address = addresses[id];
            scanCounter.Tick();

            int childCount = successors(address, ref childBuffer);
            for (int c = 0; c < childCount; c++)
            {
                ulong childAddr = childBuffer[c];
                if (childAddr == 0)
                    continue;

                (int childId, bool isNew) = GetOrAddId(childAddr);
                edgeFrom.Add(id);
                edgeTo.Add(childId);
                outDegree[id]++;

                if (isNew)
                {
                    if (budget.IsExceededBy(addresses.Count, edgeFrom.Count))
                        return ReachableGraphWalkResult.Capped();

                    frontier.Enqueue(childId);
                }
            }

            if (edgeFrom.Count >= nextEdgeCheck)
            {
                if (budget.IsExceededBy(addresses.Count, edgeFrom.Count))
                    return ReachableGraphWalkResult.Capped();

                nextEdgeCheck = edgeFrom.Count + EdgeCheckInterval;
            }
        }

        scanCounter.Complete();

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
