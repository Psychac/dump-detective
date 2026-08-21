using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing.ReverseIndex;
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
/// Single reachability walk feeding both consumers that previously ran two independent BFS passes over
/// the same reachable set — see
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §10.1:
///
/// <list type="bullet">
///   <item>Stage A (the reverse-edge index, §4/§7): streams every discovered edge to a
///   <see cref="ReverseEdgeExtractor"/>, tracking visited addresses in a plain
///   <see cref="HashSet{T}"/> — no dense ids, no in-memory CSR. This was previously
///   <c>IncrementalReachableWalker</c>, now folded in here.</item>
///   <item>Stage B (the exact dominator tree, §D2/§D4/§D6): assigns dense ids and captures a full
///   forward+reverse CSR, for <see cref="LeafFolder"/>/<c>DominatorTreeComputer</c> to consume.</item>
/// </list>
///
/// <paramref name="buildCsr"/> (see <see cref="Walk"/>) selects which of the two node-identity
/// structures the walk uses — a plain <c>HashSet&lt;ulong&gt;</c> when only Stage A is wanted (§4.1:
/// measured faster and no worse on memory than every id-map alternative tried), or a
/// <c>Dictionary&lt;ulong,int&gt;</c> plus <see cref="ChunkedBuffer{T}"/> edge-list capture when Stage B
/// is wanted too. <paramref name="reverseEdgeExtractor"/> is independent of that choice — when
/// non-null, every edge crossed is streamed to it regardless of which identity structure is in use, so
/// a caller wanting both stages together pays for <c>successors()</c> exactly once per reachable node
/// instead of running this walk twice.
///
/// No memory-usage budget is enforced here (removed per
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md's "review the budget"
/// discussion — the calibrated byte-cost model it replaced was fit to two dumps under a memory profile
/// that predated Stage A and Stage B ever sharing a walk, and its abort path could leave Stage A's
/// reverse-edge index silently incomplete). The one hard limit that remains is a correctness
/// invariant, not a policy: <see cref="ChunkedBuffer{T}"/> throws before any of Stage B's node/edge
/// counts could silently wrap past <see cref="int.MaxValue"/> — callers that want a graceful "too big,
/// skipped" outcome instead of a thrown exception need to catch around this call (see
/// <c>DiskBackedObjectIndexWriter.Build</c>'s walk-phase try/catch).
/// </summary>
internal static class ReachableGraphWalker
{
    /// <param name="rootAddresses">GC root object addresses to seed the BFS frontier from.</param>
    /// <param name="successors">Persisted forward-edge index (preferred) or live ClrMD walk (fallback).</param>
    /// <param name="reverseEdgeExtractor">
    /// When non-null, every edge crossed is streamed here (Stage A's reverse-edge index contract, §7).
    /// When null, no reverse-edge index is fed — the caller only wants the CSR (Stage B alone; every
    /// production caller passes a non-null extractor today, since Stage B only ever runs inside
    /// <c>DiskBackedObjectIndexWriter.Build</c>'s Stage A block — the null case exists for
    /// synthetic-graph unit tests that don't need a reverse-edge index at all).
    /// </param>
    /// <param name="buildCsr">
    /// When true, assigns dense ids and captures the full forward+reverse CSR for Stage B. When
    /// false, only Stage A's cheap <c>HashSet&lt;ulong&gt;</c> visited-tracking runs.
    /// </param>
    /// <param name="captureSortedAddresses">
    /// When true, <see cref="ReachableGraphWalkResult.ReachableAddresses"/> is populated with every
    /// reachable node's address, sorted ascending (§5's <c>DominatorReachableAddresses</c> persistence
    /// needs this; Phase 2's exact-tree-only callers don't, so they skip the extra sort).
    /// </param>
    public static ReachableGraphWalkResult Walk(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        ReverseEdgeExtractor? reverseEdgeExtractor,
        bool buildCsr,
        bool captureSortedAddresses,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        return buildCsr
            ? WalkWithCsr(rootAddresses, successors, reverseEdgeExtractor, captureSortedAddresses, cancellationToken, progress)
            : WalkWithoutCsr(rootAddresses, successors, reverseEdgeExtractor, captureSortedAddresses, cancellationToken, progress);
    }

    /// <summary>
    /// Stage A only (§4, §7 — formerly <c>IncrementalReachableWalker.Walk</c>): <see cref="HashSet{T}"/>
    /// visited-tracking, no dense ids, no in-memory CSR — the walk's frontier is the only reachable-set
    /// state ever held at once. Every edge crossed is streamed straight to
    /// <paramref name="reverseEdgeExtractor"/> as discovered.
    /// </summary>
    private static ReachableGraphWalkResult WalkWithoutCsr(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        ReverseEdgeExtractor? reverseEdgeExtractor,
        bool captureSortedAddresses,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress)
    {
        var visited = new HashSet<ulong>();
        var frontier = new Queue<ulong>();
        var scanCounter = new ObjectScanCounter("building reverse-edge index (reachability walk)", progress);
        var childBuffer = new ulong[64];

        int nodeCount = 0;
        long edgeCount = 0;

        foreach (ulong rootAddr in rootAddresses)
        {
            if (rootAddr == 0)
                continue;

            if (visited.Add(rootAddr))
            {
                nodeCount++;
                frontier.Enqueue(rootAddr);
            }
        }

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong address = frontier.Dequeue();
            scanCounter.Tick();

            int childCount = successors(address, ref childBuffer);
            for (int c = 0; c < childCount; c++)
            {
                ulong childAddr = childBuffer[c];
                if (childAddr == 0)
                    continue;

                reverseEdgeExtractor?.RecordEdge(address, childAddr);
                edgeCount++;

                if (visited.Add(childAddr))
                {
                    nodeCount++;
                    frontier.Enqueue(childAddr);
                }
            }
        }

        scanCounter.Complete();

        ulong[] reachableAddresses;
        if (captureSortedAddresses)
        {
            reachableAddresses = visited.ToArray();
            Array.Sort(reachableAddresses);
        }
        else
        {
            reachableAddresses = Array.Empty<ulong>();
        }

        return new ReachableGraphWalkResult(
            nodeCount: nodeCount,
            edgeCount: edgeCount,
            addresses: Array.Empty<ulong>(),
            reachableAddresses: reachableAddresses,
            outDegree: Array.Empty<int>(),
            inDegree: Array.Empty<int>(),
            isRoot: Array.Empty<bool>(),
            fwdOffsets: Array.Empty<int>(),
            fwdTargets: Array.Empty<int>(),
            revOffsets: Array.Empty<int>(),
            revTargets: Array.Empty<int>());
    }

    /// <summary>
    /// Stage B (§D2, §D4 — the walk half of the exact dominator tree's build): dense ids via
    /// <see cref="Dictionary{TKey,TValue}"/>, single-pass edge capture into <see cref="ChunkedBuffer{T}"/>,
    /// O(N+E) counting-sort CSR build at the end. When <paramref name="reverseEdgeExtractor"/> is
    /// non-null, also streams every edge to it — Stage A's contract, folded into the same pass instead
    /// of a second walk.
    /// </summary>
    private static ReachableGraphWalkResult WalkWithCsr(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        ReverseEdgeExtractor? reverseEdgeExtractor,
        bool captureSortedAddresses,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress)
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
        // ChunkedBuffer, not List<T> (§D4): at 25GB-dump scale (E ≈ 137M edges) a List<T>'s
        // double-and-copy growth would transiently hold up to ~2x the final size, with the old and
        // new backing arrays both alive during the copy — see the design doc's Measured Numbers.
        // Also the one place a runaway graph gets caught (ChunkedBuffer.Add's int.MaxValue guard) —
        // see this class's own doc comment.
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

                reverseEdgeExtractor?.RecordEdge(address, childAddr);

                (int childId, bool isNew) = GetOrAddId(childAddr);
                edgeFrom.Add(id);
                edgeTo.Add(childId);
                outDegree[id]++;

                if (isNew)
                    frontier.Enqueue(childId);
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

        ulong[] addressArray = addresses.ToArray();
        ulong[] reachableAddresses;
        if (captureSortedAddresses)
        {
            reachableAddresses = (ulong[])addressArray.Clone();
            Array.Sort(reachableAddresses);
        }
        else
        {
            reachableAddresses = Array.Empty<ulong>();
        }

        return new ReachableGraphWalkResult(
            nodeCount: nodeCount,
            edgeCount: edgeCount,
            addresses: addressArray,
            reachableAddresses: reachableAddresses,
            outDegree: outDegree.ToArray(),
            inDegree: inDegree,
            isRoot: isRoot.ToArray(),
            fwdOffsets: fwdOffsets,
            fwdTargets: fwdTargets,
            revOffsets: revOffsets,
            revTargets: revTargets);
    }
}

/// <summary>
/// Result of <see cref="ReachableGraphWalker.Walk"/>. Always a complete result — a walk either
/// finishes or throws (see the type's own doc comment on why no memory budget is enforced here, and
/// <c>DiskBackedObjectIndexWriter.Build</c>'s walk-phase try/catch for how a caller degrades a thrown
/// exception into a graceful "skipped" outcome instead).
/// </summary>
internal sealed class ReachableGraphWalkResult
{
    public int NodeCount { get; }
    public long EdgeCount { get; }
    /// <summary>Id -> address, discovery order. Empty unless the walk built a CSR.</summary>
    public ulong[] Addresses { get; }
    /// <summary>Every reachable node's address, sorted ascending. Empty unless requested.</summary>
    public ulong[] ReachableAddresses { get; }
    public int[] OutDegree { get; }
    public int[] InDegree { get; }
    /// <summary>
    /// True for nodes seeded directly from <c>rootAddresses</c> — these are LT's virtual-root
    /// children and must never be excluded by <see cref="LeafFolder"/> regardless of degree, since
    /// they have an "invisible" incoming edge from the virtual root the CSR doesn't represent.
    /// </summary>
    public bool[] IsRoot { get; }
    public int[] FwdOffsets { get; }
    public int[] FwdTargets { get; }
    public int[] RevOffsets { get; }
    public int[] RevTargets { get; }

    public ReachableGraphWalkResult(
        int nodeCount,
        long edgeCount,
        ulong[] addresses,
        ulong[] reachableAddresses,
        int[] outDegree,
        int[] inDegree,
        bool[] isRoot,
        int[] fwdOffsets,
        int[] fwdTargets,
        int[] revOffsets,
        int[] revTargets)
    {
        NodeCount = nodeCount;
        EdgeCount = edgeCount;
        Addresses = addresses;
        ReachableAddresses = reachableAddresses;
        OutDegree = outDegree;
        InDegree = inDegree;
        IsRoot = isRoot;
        FwdOffsets = fwdOffsets;
        FwdTargets = fwdTargets;
        RevOffsets = revOffsets;
        RevTargets = revTargets;
    }
}
