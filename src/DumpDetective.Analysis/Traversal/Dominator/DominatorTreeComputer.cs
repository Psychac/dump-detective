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
        LeafFoldResult fold = LeafFolder.Fold(
            graph.NodeCount, graph.OutDegree, graph.InDegree,
            graph.FwdOffsets, graph.FwdTargets, graph.RevOffsets, graph.RevTargets,
            graph.ShallowSizes, graph.IsRoot);

        int n = fold.ReducedNodeCount;
        int virtualRoot = n; // one past the reduced id space — never a real node

        var rootNewIds = new List<int>();
        var isRootNewId = new bool[n];
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            if (!graph.IsRoot[oldId])
                continue;

            // Roots are guaranteed to survive folding (LeafFolder excludes them regardless of degree).
            int newId = fold.OldToNewId[oldId];
            rootNewIds.Add(newId);
            isRootNewId[newId] = true;
        }

        IEnumerable<int> Successors(int id)
        {
            if (id == virtualRoot)
                return rootNewIds;

            return EnumerateRange(fold.ReducedFwdTargets, fold.ReducedFwdOffsets[id], fold.ReducedFwdOffsets[id + 1]);
        }

        IEnumerable<int> Predecessors(int id) => PredecessorsCore(id, fold, isRootNewId, virtualRoot);

        cancellationToken.ThrowIfCancellationRequested();
        int[] idom = LengauerTarjan.ComputeImmediateDominators(n + 1, virtualRoot, Successors, Predecessors);

        // §Phase 5: exact per-node shallow size, including folded-leaf bytes (§D8).
        var shallow = new ulong[n];
        for (int newId = 0; newId < n; newId++)
            shallow[newId] = graph.ShallowSizes[fold.NewToOldId[newId]] + fold.FoldedBytesByNewId[newId];

        // Build the dominator tree's child lists (index n = virtualRoot's own children).
        var domChildren = new List<int>[n + 1];
        for (int i = 0; i <= n; i++)
            domChildren[i] = new List<int>();
        for (int v = 0; v < n; v++)
            domChildren[idom[v]].Add(v);

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
            foreach (int c in domChildren[u])
            {
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

    private static IEnumerable<int> EnumerateRange(int[] array, int start, int end)
    {
        for (int i = start; i < end; i++)
            yield return array[i];
    }

    private static IEnumerable<int> PredecessorsCore(int id, LeafFoldResult fold, bool[] isRootNewId, int virtualRoot)
    {
        for (int e = fold.ReducedRevOffsets[id]; e < fold.ReducedRevOffsets[id + 1]; e++)
            yield return fold.ReducedRevTargets[e];

        if (isRootNewId[id])
            yield return virtualRoot;
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
