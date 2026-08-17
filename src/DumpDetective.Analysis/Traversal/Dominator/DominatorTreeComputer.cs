using DumpDetective.Analysis.Traversal;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// §Architecture steps 3-5: folds single-parent leaves (§D8), wires the result into
/// <see cref="LengauerTarjan.ComputeImmediateDominators"/> via a synthetic virtual root with one
/// edge to each real GC root (standard LT construction for multi-root graphs), then rolls up
/// exact retained bytes per node via a post-order traversal of the dominator tree.
/// </summary>
internal static class DominatorTreeComputer
{
    public static DominatorTreeComputeResult Compute(ReachableGraph graph, CancellationToken cancellationToken)
    {
        // `graph` is passed as the input holder (it implements IFoldInputs) so Fold can drop each input
        // group the moment it stops reading it, rather than holding all of them until it returns. Fold
        // allocates a full reduced CSR while the original is still reachable, and that overlap was the
        // peak of this whole path — see IFoldInputs for why a holder is required rather than the
        // array-parameter signature this replaced.
        LeafFoldResult fold = LeafFolder.Fold(graph);

        // Safety net: idempotent, and covers the release points Fold didn't reach if it returned early.
        // `graph` itself stays alive for the rest of this computation (Addresses/MethodTables/
        // ShallowSizes/IsRoot are still needed), so the arrays must be dropped explicitly rather than
        // waiting for `graph` to go out of scope.
        graph.ReleaseEdgeAndDegreeArrays();

        int n = fold.ReducedNodeCount;
        int virtualRoot = n; // one past the reduced id space — never a real node

        var rootNewIdsList = new List<int>();
        var isRootNewId = new bool[n];
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            if (!graph.IsRoot[oldId])
                continue;

            // Roots are guaranteed to survive folding (LeafFolder excludes them regardless of degree).
            int newId = fold.OldToNewId[oldId];
            rootNewIdsList.Add(newId);
            isRootNewId[newId] = true;
        }
        int[] rootNewIds = rootNewIdsList.ToArray();

        // Bake the virtual root's edges directly into an extended reverse CSR (each root gets one
        // extra predecessor: the virtual root) instead of special-casing it per call — lets
        // Predecessors(id) return a single contiguous ReadOnlySpan<int> slice with no per-call
        // allocation, avoiding the IEnumerator<int>/yield-return allocation LengauerTarjan's DFS
        // and semidominator loop would otherwise incur once per node (tens of millions of small
        // objects at real-dump scale — see design doc's Measured Numbers).
        // Built directly from the reduced FORWARD CSR plus per-node in-degrees. Previously LeafFolder
        // materialised a reduced reverse CSR and this loop copied it wholesale, adding one slot per root
        // — a third full E'-sized copy of the edge set (~61 MB on a 3.3GB dump, ~440 MB on a 25.6GB one)
        // that existed only to gain those slots. Same counting-sort shape, one array and one pass fewer.
        var extRevOffsets = new int[n + 1];
        for (int i = 0; i < n; i++)
            extRevOffsets[i + 1] = extRevOffsets[i] + fold.ReducedInDegree[i] + (isRootNewId[i] ? 1 : 0);

        var extRevTargets = new int[extRevOffsets[n]];
        var extRevCursor = (int[])extRevOffsets.Clone();

        // Real predecessors first, then the virtual root appended per rooted node. Slot order within a
        // predecessor list is irrelevant to LT — the semidominator loop takes a minimum over the whole
        // list — but keeping the previous layout means the existing dominator-tree tests remain a
        // byte-for-byte regression check on this rewrite.
        for (int newFrom = 0; newFrom < n; newFrom++)
        {
            for (int e = fold.ReducedFwdOffsets[newFrom]; e < fold.ReducedFwdOffsets[newFrom + 1]; e++)
            {
                int newTo = fold.ReducedFwdTargets[e];
                extRevTargets[extRevCursor[newTo]++] = newFrom;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (isRootNewId[i])
                extRevTargets[extRevCursor[i]++] = virtualRoot;
        }

        // In-degrees have no reader past this point.
        fold.ReleaseReducedInDegree();

        ReadOnlySpan<int> Successors(int id)
        {
            if (id == virtualRoot)
                return rootNewIds;

            return new ReadOnlySpan<int>(fold.ReducedFwdTargets, fold.ReducedFwdOffsets[id], fold.ReducedFwdOffsets[id + 1] - fold.ReducedFwdOffsets[id]);
        }

        // virtualRoot itself is never queried for predecessors (the main semidominator loop only
        // ever visits real reduced-id nodes), so extRevOffsets doesn't need an entry for it.
        ReadOnlySpan<int> Predecessors(int id) => new ReadOnlySpan<int>(extRevTargets, extRevOffsets[id], extRevOffsets[id + 1] - extRevOffsets[id]);

        cancellationToken.ThrowIfCancellationRequested();
        int[] idom = LengauerTarjan.ComputeImmediateDominators(n + 1, virtualRoot, Successors, Predecessors);

        // §Phase 5: exact per-node shallow size, including folded-leaf bytes (§D8).
        var shallow = new ulong[n];
        for (int newId = 0; newId < n; newId++)
            shallow[newId] = graph.ShallowSizes[fold.NewToOldId[newId]] + fold.FoldedBytesByNewId[newId];

        // Build the dominator tree's child adjacency as CSR (offsets+targets) via counting sort —
        // same pattern LeafFolder/ReachableGraphWalker already use — instead of a List<int> per
        // node. A `List<int>[n + 1]` here would allocate one heap object (list + backing array)
        // per dominator-tree node: at 58M-node scale that's tens of millions of small objects for
        // no reason, since every node has exactly one parent (`idom[v]`) and the full child count
        // per parent is known after a single counting pass.
        var childCount = new int[n + 1]; // index n = virtualRoot's own child count
        for (int v = 0; v < n; v++)
            childCount[idom[v]]++;

        var childOffsets = new int[n + 2];
        for (int i = 0; i <= n; i++)
            childOffsets[i + 1] = childOffsets[i] + childCount[i];

        var childTargets = new int[n];
        var childCursor = (int[])childOffsets.Clone();
        for (int v = 0; v < n; v++)
            childTargets[childCursor[idom[v]]++] = v;

        // Preorder traversal from the virtual root (iterative, no recursion — safe at 58M-node scale).
        var preorder = new List<int>(n + 1);
        var depth = new int[n + 1];
        var stack = new Stack<int>();
        stack.Push(virtualRoot);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int u = stack.Pop();
            preorder.Add(u);
            for (int e = childOffsets[u]; e < childOffsets[u + 1]; e++)
            {
                int c = childTargets[e];
                depth[c] = u == virtualRoot ? 0 : depth[u] + 1;
                stack.Push(c);
            }
        }

        // Retained bytes = subtree sum. Processing preorder in reverse guarantees every descendant
        // of v has already had its own contribution folded into retained[v] before v propagates up
        // to its own parent — standard preorder-reversed subtree-sum technique, no recursion needed.
        var retained = new ulong[n];
        for (int v = 0; v < n; v++)
            retained[v] = shallow[v];

        for (int i = preorder.Count - 1; i >= 0; i--)
        {
            int v = preorder[i];
            if (v == virtualRoot)
                continue;

            int parent = idom[v];
            if (parent != virtualRoot)
                retained[parent] += retained[v];
        }

        return new DominatorTreeComputeResult(fold, idom, retained, depth, virtualRoot);
    }
}

/// <summary>
/// Result of <see cref="DominatorTreeComputer.Compute"/> — indexed by the *reduced* (post-fold) id
/// space; use <see cref="LeafFold"/>'s <c>NewToOldId</c>/<c>OldToNewId</c> to translate back to
/// <see cref="ReachableGraph"/> ids when mapping to addresses for the report.
/// </summary>
internal sealed class DominatorTreeComputeResult
{
    public LeafFoldResult LeafFold { get; }
    /// <summary>New id -> immediate dominator's new id, or <see cref="VirtualRoot"/> for a direct child of the virtual root.</summary>
    public int[] Idom { get; }
    /// <summary>New id -> exact retained bytes (subtree sum, folded-leaf bytes included).</summary>
    public ulong[] RetainedBytes { get; }
    /// <summary>New id -> dominator-tree depth (0 for a direct child of the virtual root).</summary>
    public int[] Depth { get; }
    public int VirtualRoot { get; }

    public DominatorTreeComputeResult(LeafFoldResult leafFold, int[] idom, ulong[] retainedBytes, int[] depth, int virtualRoot)
    {
        LeafFold = leafFold;
        Idom = idom;
        RetainedBytes = retainedBytes;
        Depth = depth;
        VirtualRoot = virtualRoot;
    }
}
