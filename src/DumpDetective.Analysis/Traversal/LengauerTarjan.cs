namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Classic iterative Lengauer-Tarjan dominator-tree algorithm (the "simple", O(E log V) path-
/// compression variant — not the Sparse Evaluation Graph optimization, unnecessary at the node
/// counts this project targets; see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md). Operates on dense
/// <c>int</c> node ids with injected successor/predecessor functions — deliberately heap-agnostic
/// (no <c>ClrObject</c>/<c>ClrHeap</c> dependency) so it is unit-testable with small hand-built
/// graphs, matching the pattern established by <see cref="BidirectionalGraphSearch"/>. The
/// production caller (not yet wired — see the design doc's rollout plan) supplies successors from
/// the packed CSR forward array and predecessors from the packed CSR reverse array built by the
/// single-pass reachability walk (<c>tools/DominatorSpike ... packed1</c>), not by re-walking
/// <c>ClrObject</c> fields here.
/// </summary>
internal static class LengauerTarjan
{
    /// <summary>
    /// Computes the immediate-dominator relation for every node reachable from <paramref name="root"/>.
    /// </summary>
    /// <param name="nodeCount">Upper bound on node ids (ids must be in <c>[0, nodeCount)</c>).</param>
    /// <param name="root">The id of the (real or virtual) root node — see design doc §Scope § Root set.</param>
    /// <param name="successors">Forward neighbor lookup (outgoing edges).</param>
    /// <param name="predecessors">
    /// Backward neighbor lookup (incoming edges) — must be the <em>true, uncapped</em> predecessor
    /// set, not a fanout-truncated index (see design doc §Phase 3: the disk-backed reverse-edge
    /// index's 10K-parents-per-child cap is unsafe here and must not be used as this parameter's
    /// source in production).
    /// </param>
    /// <returns>
    /// <c>idom[]</c> indexed by node id: <c>idom[root] == root</c> (no dominator above the root);
    /// <c>idom[v] == -1</c> for any node not reachable from <paramref name="root"/>; otherwise the
    /// immediate dominator of <c>v</c>.
    /// </returns>
    public static int[] ComputeImmediateDominators(
        int nodeCount,
        int root,
        Func<int, IEnumerable<int>> successors,
        Func<int, IEnumerable<int>> predecessors)
    {
        var idomByNode = new int[nodeCount];
        Array.Fill(idomByNode, -1);

        if (nodeCount == 0)
            return idomByNode;

        // --- DFS numbering (iterative — no recursion, safe at arbitrary reachable-node scale) ---
        var dfsNumByNode = new int[nodeCount];
        Array.Fill(dfsNumByNode, -1);

        // Reachable-node upper bound is nodeCount; vertexByDfs/parentByDfs are sized to it and
        // only the first `n` (reachable count) entries are meaningful.
        var vertexByDfs = new int[nodeCount];
        var dfsParentByDfs = new int[nodeCount];

        int n = 0;
        var dfsStack = new Stack<(int Node, IEnumerator<int> Successors)>();

        dfsNumByNode[root] = n;
        vertexByDfs[n] = root;
        dfsParentByDfs[n] = -1;
        n++;
        dfsStack.Push((root, successors(root).GetEnumerator()));

        while (dfsStack.Count > 0)
        {
            (int node, IEnumerator<int> iter) = dfsStack.Peek();
            if (iter.MoveNext())
            {
                int child = iter.Current;
                if (dfsNumByNode[child] == -1)
                {
                    dfsNumByNode[child] = n;
                    vertexByDfs[n] = child;
                    dfsParentByDfs[n] = dfsNumByNode[node];
                    n++;
                    dfsStack.Push((child, successors(child).GetEnumerator()));
                }
            }
            else
            {
                dfsStack.Pop();
            }
        }

        if (n == 1)
        {
            // Only the root is reachable.
            idomByNode[root] = root;
            return idomByNode;
        }

        // --- Lengauer-Tarjan proper, worked entirely in DFS-number space [0, n) ---
        var semi = new int[n];
        var label = new int[n];
        var ancestor = new int[n];
        var idomByDfs = new int[n];
        var buckets = new List<int>?[n]; // buckets[semiDfsNum] -> child dfs numbers awaiting idom resolution

        for (int i = 0; i < n; i++)
        {
            semi[i] = i;
            label[i] = i;
            ancestor[i] = -1;
        }

        int Eval(int v)
        {
            if (ancestor[v] == -1)
                return v;

            Compress(v);
            return label[v];
        }

        void Compress(int v)
        {
            var path = new Stack<int>();
            int cur = v;
            while (ancestor[cur] != -1 && ancestor[ancestor[cur]] != -1)
            {
                path.Push(cur);
                cur = ancestor[cur];
            }

            while (path.Count > 0)
            {
                int node = path.Pop();
                int anc = ancestor[node];
                if (semi[label[anc]] < semi[label[node]])
                    label[node] = label[anc];
                ancestor[node] = ancestor[anc];
            }
        }

        for (int wDfs = n - 1; wDfs >= 1; wDfs--)
        {
            int w = vertexByDfs[wDfs];
            int wParentDfs = dfsParentByDfs[wDfs];

            foreach (int v in predecessors(w))
            {
                int vDfs = dfsNumByNode[v];
                if (vDfs == -1)
                    continue; // predecessor not reachable from root — irrelevant to dominance here

                int uDfs = Eval(vDfs);
                if (semi[uDfs] < semi[wDfs])
                    semi[wDfs] = semi[uDfs];
            }

            (buckets[semi[wDfs]] ??= new List<int>()).Add(wDfs);
            ancestor[wDfs] = wParentDfs;

            List<int>? parentBucket = buckets[wParentDfs];
            if (parentBucket is not null)
            {
                foreach (int vDfs in parentBucket)
                {
                    int uDfs = Eval(vDfs);
                    idomByDfs[vDfs] = semi[uDfs] < semi[vDfs] ? uDfs : wParentDfs;
                }
                parentBucket.Clear();
            }
        }

        // idomByDfs[i] currently holds a DFS number (either uDfs or wParentDfs from the main loop
        // above), never a node id — semi[i] is also a DFS number, so this comparison and the
        // dependent-lookup below must stay in DFS-number space throughout. idomByDfs[i] is always
        // < i (it's an ancestor in the DFS tree), so resolving in increasing i order guarantees
        // idomByDfs[idomByDfs[i]] is already finalized by the time it's read.
        for (int i = 1; i < n; i++)
        {
            if (idomByDfs[i] != semi[i])
                idomByDfs[i] = idomByDfs[idomByDfs[i]];

            idomByNode[vertexByDfs[i]] = vertexByDfs[idomByDfs[i]];
        }

        idomByNode[root] = root;
        return idomByNode;
    }
}
