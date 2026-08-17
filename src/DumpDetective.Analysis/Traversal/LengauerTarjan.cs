namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Neighbor lookup for <see cref="LengauerTarjan"/> — returns a <see cref="ReadOnlySpan{T}"/> over
/// whatever backing array the caller already owns (typically a CSR target-array slice), so
/// <c>foreach</c> over the result compiles to a plain indexed loop with no <c>IEnumerator&lt;T&gt;</c>
/// allocation. A regular (non-generic) delegate can carry a <c>ref struct</c> return type like
/// <see cref="ReadOnlySpan{T}"/> without issue — only generic delegates such as <c>Func&lt;,&gt;</c>
/// can't.
/// </summary>
internal delegate ReadOnlySpan<int> NeighborsFunc(int id);

/// <summary>
/// Classic iterative Lengauer-Tarjan dominator-tree algorithm (the "simple", O(E log V) path-
/// compression variant — not the Sparse Evaluation Graph optimization, unnecessary at the node
/// counts this project targets; see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md). Operates on dense
/// <c>int</c> node ids with injected successor/predecessor functions — deliberately heap-agnostic
/// (no <c>ClrObject</c>/<c>ClrHeap</c> dependency) so it is unit-testable with small hand-built
/// graphs, matching the pattern established by <see cref="BidirectionalGraphSearch"/>. The
/// production caller supplies successors from the packed CSR forward array and predecessors from
/// the packed CSR reverse array built by the single-pass reachability walk — see
/// <see cref="Dominator.DominatorTreeComputer"/>.
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
        NeighborsFunc successors,
        NeighborsFunc predecessors)
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
        // Explicit (node, cursor) stack instead of (node, IEnumerator<int>) — re-deriving the
        // ReadOnlySpan<int> from `successors(node)` on each step is just an offset/length lookup
        // (no allocation), unlike an IEnumerator<int> which needs a heap object to carry its
        // resumable state. A ReadOnlySpan itself can't be stored in a Stack<T> (ref struct), so the
        // cursor position is all that needs to persist.
        var stackNode = new int[nodeCount + 1];
        var stackCursor = new int[nodeCount + 1];
        int sp = 0;

        dfsNumByNode[root] = n;
        vertexByDfs[n] = root;
        dfsParentByDfs[n] = -1;
        n++;
        stackNode[sp] = root;
        stackCursor[sp] = 0;
        sp++;

        while (sp > 0)
        {
            int node = stackNode[sp - 1];
            ReadOnlySpan<int> neighbors = successors(node);
            int cursor = stackCursor[sp - 1];

            if (cursor < neighbors.Length)
            {
                int child = neighbors[cursor];
                stackCursor[sp - 1] = cursor + 1;

                if (dfsNumByNode[child] == -1)
                {
                    dfsNumByNode[child] = n;
                    vertexByDfs[n] = child;
                    dfsParentByDfs[n] = dfsNumByNode[node];
                    n++;
                    stackNode[sp] = child;
                    stackCursor[sp] = 0;
                    sp++;
                }
            }
            else
            {
                sp--;
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

            ReadOnlySpan<int> preds = predecessors(w);
            for (int p = 0; p < preds.Length; p++)
            {
                int v = preds[p];
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
