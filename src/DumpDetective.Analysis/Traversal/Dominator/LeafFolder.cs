namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// §D8: excludes single-parent leaves (out-degree 0, in-degree 1 — "foldable") from the node set
/// fed to Lengauer-Tarjan, folding each one's shallow size into its sole parent's accumulator
/// instead of giving it a full node/LT-array slot. Purely graph-structural — no
/// <c>MethodTableHasOutgoingRefs</c>/type-metadata lookup needed, just the degree arrays
/// <see cref="ReachableGraphWalker"/> already builds. Nodes with out-degree 0 and in-degree &gt;1
/// (shared leaves) are never folded — determining their <c>idom</c> is exactly what LT's
/// semidominator computation exists to do, and no local rule can substitute for it.
///
/// Operates on plain arrays (no <c>ReachableGraph</c>/ClrHeap dependency), so it's directly
/// unit-testable with hand-built graphs, same pattern as <c>LengauerTarjanTests</c>.
/// </summary>
/// <summary>
/// <see cref="LeafFolder.Fold"/>'s inputs, behind a holder that can drop each group the moment Fold
/// stops reading it. Fold allocates a complete reduced CSR (2 x E' ints) while the original CSR
/// (2 x E ints) is still reachable, and that overlap is the peak of the whole exact-tree path —
/// ~2 GB of it on a 25.6 GB dump.
///
/// <para><b>Why a holder rather than array parameters plus release callbacks.</b> That was tried first
/// and <b>measured as freeing exactly 0 bytes</b> of 91.8 MB, with a compacting gen2 collection on
/// either side of the release. The arrays were passed as ten individual parameters, so on x64 most of
/// them live in the caller's outgoing-argument stack slots for the duration of the call; those slots
/// remain GC-reachable, and no assignment inside the callee can clear them. Routing every array through
/// one object makes the holder's field the only reference, so clearing it actually frees the array. The
/// lesson generalises: "drop the reference" only works if you can reach every reference.</para>
///
/// <para>An interface rather than a concrete type so <see cref="LeafFolder"/> stays decoupled from
/// <c>ReachableGraph</c> and unit-testable — see <see cref="ArrayFoldInputs"/>. See
/// docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 3.2.</para>
/// </summary>
internal interface IFoldInputs
{
    int NodeCount { get; }
    int[] OutDegree { get; }
    int[] InDegree { get; }
    int[] FwdOffsets { get; }
    int[] FwdTargets { get; }
    int[] RevOffsets { get; }
    int[] RevTargets { get; }
    ulong[] ShallowSizes { get; }
    bool[] IsRoot { get; }

    /// <summary>Foldable-leaf scan complete: <see cref="OutDegree"/>/<see cref="InDegree"/> are dead.</summary>
    void ReleaseDegreeArrays();

    /// <summary>Folded bytes attributed: <see cref="RevOffsets"/>/<see cref="RevTargets"/> are dead.</summary>
    void ReleaseReverseEdgeArrays();

    /// <summary>Reduced forward CSR filled: <see cref="FwdOffsets"/>/<see cref="FwdTargets"/> are dead.</summary>
    void ReleaseForwardEdgeArrays();
}

/// <summary>
/// Plain-array <see cref="IFoldInputs"/> for tests and for callers that don't own a
/// <c>ReachableGraph</c>. Keeps <see cref="LeafFolder"/> unit-testable with hand-built graphs, the
/// property the original array-parameter signature existed to provide.
/// </summary>
internal sealed class ArrayFoldInputs : IFoldInputs
{
    public int NodeCount { get; }
    public int[] OutDegree { get; private set; }
    public int[] InDegree { get; private set; }
    public int[] FwdOffsets { get; private set; }
    public int[] FwdTargets { get; private set; }
    public int[] RevOffsets { get; private set; }
    public int[] RevTargets { get; private set; }
    public ulong[] ShallowSizes { get; }
    public bool[] IsRoot { get; }

    public ArrayFoldInputs(
        int nodeCount,
        int[] outDegree,
        int[] inDegree,
        int[] fwdOffsets,
        int[] fwdTargets,
        int[] revOffsets,
        int[] revTargets,
        ulong[] shallowSizes,
        bool[]? isRoot = null)
    {
        NodeCount = nodeCount;
        OutDegree = outDegree;
        InDegree = inDegree;
        FwdOffsets = fwdOffsets;
        FwdTargets = fwdTargets;
        RevOffsets = revOffsets;
        RevTargets = revTargets;
        ShallowSizes = shallowSizes;
        IsRoot = isRoot ?? new bool[nodeCount];
    }

    public void ReleaseDegreeArrays()
    {
        OutDegree = Array.Empty<int>();
        InDegree = Array.Empty<int>();
    }

    public void ReleaseReverseEdgeArrays()
    {
        RevOffsets = Array.Empty<int>();
        RevTargets = Array.Empty<int>();
    }

    public void ReleaseForwardEdgeArrays()
    {
        FwdOffsets = Array.Empty<int>();
        FwdTargets = Array.Empty<int>();
    }
}

internal static class LeafFolder
{
    /// <summary>
    /// Forced compacting gen2 collection then a heap reading. LOH compaction is mandatory here: every
    /// array involved is far over the 85KB LOH threshold and the LOH is swept, not compacted, by
    /// default — so without CompactOnce a freed array's bytes stay counted as free-list space and the
    /// measurement reports no change whether the release happened or not.
    /// </summary>
    private static long CompactAndMeasure()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    /// <summary>
    /// Convenience overload for callers holding plain arrays (tests, and anything without a
    /// <c>ReachableGraph</c>). Wraps them in an <see cref="ArrayFoldInputs"/> so the single code path
    /// below is the one that gets exercised.
    /// </summary>
    public static LeafFoldResult Fold(
        int nodeCount,
        int[] outDegree,
        int[] inDegree,
        int[] fwdOffsets,
        int[] fwdTargets,
        int[] revOffsets,
        int[] revTargets,
        ulong[] shallowSizes,
        bool[]? isRoot = null)
        => Fold(new ArrayFoldInputs(
            nodeCount, outDegree, inDegree, fwdOffsets, fwdTargets, revOffsets, revTargets, shallowSizes, isRoot));

    public static LeafFoldResult Fold(IFoldInputs inputs)
    {
        int nodeCount = inputs.NodeCount;
        ulong[] shallowSizes = inputs.ShallowSizes;
        bool[] isRoot = inputs.IsRoot;

        // Each array is read through a phase-local, then both the local and the holder's field are
        // cleared. Two references, both reachable from here — unlike the array-parameter design this
        // replaced, where the caller's outgoing-argument stack slots were unreachable from inside Fold
        // and the release freed nothing (see IFoldInputs).
        int[] outDegree = inputs.OutDegree;
        int[] inDegree = inputs.InDegree;
        long originalEdgeCount = inputs.FwdTargets.LongLength;

        var isFoldable = new bool[nodeCount];
        int foldableCount = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            // A GC root must never be folded even if out-degree 0 and in-degree 1: it has an
            // "invisible" incoming edge from LT's virtual root that this CSR doesn't represent, so
            // folding it into its one real-object parent would silently misattribute its
            // directly-rooted status (and retained bytes) to that parent instead.
            if (isRoot[i])
                continue;

            if (outDegree[i] == 0 && inDegree[i] == 1)
            {
                isFoldable[i] = true;
                foldableCount++;
            }
        }

        // outDegree/inDegree are read only by the scan above. Clear both the holder's field and the
        // phase-local, so no reference to either array survives this point.
        inputs.ReleaseDegreeArrays();
        outDegree = Array.Empty<int>();
        inDegree = Array.Empty<int>();

        // Assign dense new ids to surviving (non-foldable) nodes only, preserving relative order.
        var oldToNewId = new int[nodeCount];
        var newToOldId = new int[nodeCount - foldableCount];
        int nextNewId = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            if (isFoldable[i])
            {
                oldToNewId[i] = -1;
                continue;
            }

            oldToNewId[i] = nextNewId;
            newToOldId[nextNewId] = i;
            nextNewId++;
        }

        int reducedNodeCount = nextNewId;
        var foldedBytesByNewId = new ulong[reducedNodeCount];

        // Fold each leaf's shallow size into its sole parent. A foldable leaf's single predecessor
        // is guaranteed to survive: any node with an edge to another node has out-degree >= 1, so it
        // can never itself be foldable (foldable requires out-degree == 0).
        int[] revOffsetsLocal = inputs.RevOffsets;
        int[] revTargetsLocal = inputs.RevTargets;
        for (int i = 0; i < nodeCount; i++)
        {
            if (!isFoldable[i])
                continue;

            int parentOldId = revTargetsLocal[revOffsetsLocal[i]]; // inDegree[i] == 1 — exactly one entry
            int parentNewId = oldToNewId[parentOldId];
            foldedBytesByNewId[parentNewId] += shallowSizes[i];
        }

        // The reverse CSR's only purpose in Fold is the single-predecessor lookup above; the reduced
        // reverse CSR built at the end is derived from the reduced *forward* CSR, not from this one.
        // Releasing here means the reducedFwdTargets allocation below can reuse the space.
        inputs.ReleaseReverseEdgeArrays();
        revOffsetsLocal = Array.Empty<int>();
        revTargetsLocal = Array.Empty<int>();

        // Rebuild forward CSR over the reduced id space, dropping any edge whose target was folded
        // away (its contribution is already captured in foldedBytesByNewId).
        int[] fwdOffsetsLocal = inputs.FwdOffsets;
        int[] fwdTargetsLocal = inputs.FwdTargets;

        var reducedOutDegree = new int[reducedNodeCount];
        for (int newId = 0; newId < reducedNodeCount; newId++)
        {
            int oldId = newToOldId[newId];
            for (int e = fwdOffsetsLocal[oldId]; e < fwdOffsetsLocal[oldId + 1]; e++)
            {
                if (oldToNewId[fwdTargetsLocal[e]] >= 0)
                    reducedOutDegree[newId]++;
            }
        }

        var reducedFwdOffsets = new int[reducedNodeCount + 1];
        for (int newId = 0; newId < reducedNodeCount; newId++)
            reducedFwdOffsets[newId + 1] = reducedFwdOffsets[newId] + reducedOutDegree[newId];

        long reducedEdgeCount = reducedFwdOffsets[reducedNodeCount];
        var reducedFwdTargets = new int[reducedEdgeCount];
        var reducedInDegree = new int[reducedNodeCount];
        var cursor = (int[])reducedFwdOffsets.Clone();

        for (int newId = 0; newId < reducedNodeCount; newId++)
        {
            int oldId = newToOldId[newId];
            for (int e = fwdOffsetsLocal[oldId]; e < fwdOffsetsLocal[oldId + 1]; e++)
            {
                int oldTarget = fwdTargetsLocal[e];
                int newTarget = oldToNewId[oldTarget];
                if (newTarget < 0)
                    continue;

                reducedFwdTargets[cursor[newId]++] = newTarget;
                reducedInDegree[newTarget]++;
            }
        }

        // Original forward CSR fully consumed. This is the release that matters most: it happens
        // immediately before reducedRevTargets (another E'-sized array) is allocated below.
        bool probe = Environment.GetEnvironmentVariable("DD_PERF_DOMINATOR_PEAK") == "1";
        long liveBeforeRelease = probe ? CompactAndMeasure() : 0;

        inputs.ReleaseForwardEdgeArrays();
        fwdOffsetsLocal = Array.Empty<int>();
        fwdTargetsLocal = Array.Empty<int>();

        if (probe)
        {
            long liveAfterRelease = CompactAndMeasure();
            Console.Error.WriteLine(
                $"[PERF] LeafFolder forward-CSR release: live {liveBeforeRelease / (1024.0 * 1024):N1} MB -> " +
                $"{liveAfterRelease / (1024.0 * 1024):N1} MB (freed {(liveBeforeRelease - liveAfterRelease) / (1024.0 * 1024):N1} MB; " +
                $"arrays were 4x(N+1)+4xE = {(4L * (nodeCount + 1) + 4L * originalEdgeCount) / (1024.0 * 1024):N1} MB, " +
                $"holder={inputs.GetType().Name})");
        }

        // Gated peak-live probe. This is the structural peak of the whole exact-tree path: the reduced
        // forward CSR is fully built and the equally-large reduced reverse CSR is about to be allocated.
        // Forces a full collection so the figure is actual *live* bytes rather than heap size inflated
        // by uncollected garbage — which is the only way to see the effect of releasing inputs early,
        // since that changes reachability, not allocation totals. Expensive by design, hence the gate.
        if (Environment.GetEnvironmentVariable("DD_PERF_DOMINATOR_PEAK") == "1")
        {
            // LOH compaction is required, not optional, for this figure to mean anything: every array
            // here is far over the 85KB LOH threshold, and the LOH is swept rather than compacted by
            // default. Without CompactOnce, bytes freed by releasing inputs early stay counted in
            // GetTotalMemory as free-list space, and the probe reports an identical number whether the
            // release happened or not — measured exactly that way before this was corrected.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            Console.Error.WriteLine(
                $"[PERF] LeafFolder peak-live before reduced reverse CSR: " +
                $"{GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024):N1} MB live " +
                $"(N={nodeCount:N0}, N'={reducedNodeCount:N0}, E'={reducedEdgeCount:N0}, " +
                $"holder={inputs.GetType().Name})");
        }

        var reducedRevOffsets = new int[reducedNodeCount + 1];
        for (int newId = 0; newId < reducedNodeCount; newId++)
            reducedRevOffsets[newId + 1] = reducedRevOffsets[newId] + reducedInDegree[newId];

        var reducedRevTargets = new int[reducedEdgeCount];
        var revCursor = (int[])reducedRevOffsets.Clone();
        for (int newFrom = 0; newFrom < reducedNodeCount; newFrom++)
        {
            for (int e = reducedFwdOffsets[newFrom]; e < reducedFwdOffsets[newFrom + 1]; e++)
            {
                int newTo = reducedFwdTargets[e];
                reducedRevTargets[revCursor[newTo]++] = newFrom;
            }
        }

        return new LeafFoldResult(
            reducedNodeCount,
            oldToNewId,
            newToOldId,
            reducedFwdOffsets,
            reducedFwdTargets,
            reducedRevOffsets,
            reducedRevTargets,
            foldedBytesByNewId);
    }
}

/// <summary>Result of <see cref="LeafFolder.Fold"/> — the LT-facing reduced graph.</summary>
internal sealed class LeafFoldResult
{
    public int ReducedNodeCount { get; }
    /// <summary>Length = original <c>nodeCount</c>; -1 for folded-away (foldable leaf) nodes.</summary>
    public int[] OldToNewId { get; }
    /// <summary>Length = <see cref="ReducedNodeCount"/>.</summary>
    public int[] NewToOldId { get; }
    public int[] ReducedFwdOffsets { get; }
    public int[] ReducedFwdTargets { get; }
    public int[] ReducedRevOffsets { get; private set; }
    public int[] ReducedRevTargets { get; private set; }
    /// <summary>
    /// Extra shallow-size bytes folded into each surviving node from its foldable-leaf children,
    /// indexed by new id — add to the node's own subtree sum during the retained-bytes rollup.
    /// </summary>
    public ulong[] FoldedBytesByNewId { get; }

    /// <summary>
    /// Drops the reduced reverse CSR once <see cref="DominatorTreeComputer"/> has folded it into the
    /// virtual-root-extended copy Lengauer-Tarjan actually queries. Nothing reads
    /// <see cref="ReducedRevOffsets"/>/<see cref="ReducedRevTargets"/> after that point, but this object
    /// stays rooted for the whole computation (LT reads the *forward* arrays throughout, and the caller
    /// reads the id maps), so without this the E'-sized reverse arrays sit alive and unused alongside
    /// the extended copy of themselves. Replaces with <see cref="Array.Empty{T}"/> rather than nulling
    /// so the properties stay non-nullable, matching <c>ReachableGraph</c>'s pattern.
    /// </summary>
    internal void ReleaseReducedReverseArrays()
    {
        ReducedRevOffsets = Array.Empty<int>();
        ReducedRevTargets = Array.Empty<int>();
    }

    public LeafFoldResult(
        int reducedNodeCount,
        int[] oldToNewId,
        int[] newToOldId,
        int[] reducedFwdOffsets,
        int[] reducedFwdTargets,
        int[] reducedRevOffsets,
        int[] reducedRevTargets,
        ulong[] foldedBytesByNewId)
    {
        ReducedNodeCount = reducedNodeCount;
        OldToNewId = oldToNewId;
        NewToOldId = newToOldId;
        ReducedFwdOffsets = reducedFwdOffsets;
        ReducedFwdTargets = reducedFwdTargets;
        ReducedRevOffsets = reducedRevOffsets;
        ReducedRevTargets = reducedRevTargets;
        FoldedBytesByNewId = foldedBytesByNewId;
    }
}
