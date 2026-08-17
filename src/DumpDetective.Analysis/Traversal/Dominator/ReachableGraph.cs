using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// The reachable graph plus per-node metadata needed downstream (§Architecture step 2-3) — output
/// of <see cref="ReachableGraphBuilder.Build"/>, input to <see cref="LeafFolder"/> and eventually
/// Lengauer-Tarjan.
/// </summary>
internal sealed class ReachableGraph : IFoldInputs
{
    public int NodeCount { get; }
    /// <summary>
    /// Edge count captured at construction so it survives <see cref="ReleaseEdgeAndDegreeArrays"/> —
    /// the exact-tree path's memory cost is dominated by the per-edge arrays (§D6), so the budget
    /// model can't be validated without knowing E, and after the release `FwdTargets.Length` is 0.
    /// </summary>
    public long EdgeCount { get; }
    public ulong[] Addresses { get; }          // id -> Address
    public ulong[] MethodTables { get; }       // id -> MethodTable
    public ulong[] ShallowSizes { get; }       // id -> ShallowSize
    public GenerationTag[] GenerationTags { get; } // id -> report-filter tag (§D1 — never graph membership)
    public bool[] IsRoot { get; }
    public int[] OutDegree { get; private set; }
    public int[] InDegree { get; private set; }
    public int[] FwdOffsets { get; private set; }
    public int[] FwdTargets { get; private set; }
    public int[] RevOffsets { get; private set; }
    public int[] RevTargets { get; private set; }

    public ReachableGraph(
        ReachableGraphWalkResult walkResult,
        ulong[] methodTables,
        ulong[] shallowSizes,
        GenerationTag[] generationTags)
    {
        NodeCount = walkResult.NodeCount;
        EdgeCount = walkResult.FwdTargets.Length;
        Addresses = walkResult.Addresses;
        IsRoot = walkResult.IsRoot;
        OutDegree = walkResult.OutDegree;
        InDegree = walkResult.InDegree;
        FwdOffsets = walkResult.FwdOffsets;
        FwdTargets = walkResult.FwdTargets;
        RevOffsets = walkResult.RevOffsets;
        RevTargets = walkResult.RevTargets;
        MethodTables = methodTables;
        ShallowSizes = shallowSizes;
        GenerationTags = generationTags;
    }

    /// <summary>
    /// Drops the raw edge/degree arrays once <see cref="LeafFolder.Fold"/> has consumed them to
    /// build the reduced CSR — nothing downstream of that point (the LT computation, the
    /// retained-bytes rollup, or the report's per-type aggregation) reads these again, but the
    /// `ReachableGraph` object itself typically stays rooted for the rest of the exact-tree
    /// computation (its `Addresses`/`MethodTables`/`ShallowSizes` are still needed). Without this,
    /// these arrays — sized to the full edge count `E`, not the node count — sit alive and unused
    /// for the whole computation. Replaces with <see cref="Array.Empty{T}"/> rather than
    /// nulling so the properties stay non-nullable for existing callers.
    /// </summary>
    internal void ReleaseEdgeAndDegreeArrays()
    {
        ReleaseDegreeArrays();
        ReleaseForwardEdgeArrays();
        ReleaseReverseEdgeArrays();
    }

    /// <summary>
    /// <see cref="IFoldInputs"/>: called by <see cref="LeafFolder.Fold"/> partway through, so the
    /// reduced CSR it allocates can reuse this memory instead of stacking on top of it. Each is
    /// idempotent — <see cref="ReleaseEdgeAndDegreeArrays"/> still runs afterwards as a safety net for
    /// callers that pass no releaser.
    /// </summary>
    public void ReleaseDegreeArrays()
    {
        OutDegree = Array.Empty<int>();
        InDegree = Array.Empty<int>();
    }

    /// <inheritdoc cref="ReleaseDegreeArrays"/>
    public void ReleaseForwardEdgeArrays()
    {
        FwdOffsets = Array.Empty<int>();
        FwdTargets = Array.Empty<int>();
    }

    /// <inheritdoc cref="ReleaseDegreeArrays"/>
    public void ReleaseReverseEdgeArrays()
    {
        RevOffsets = Array.Empty<int>();
        RevTargets = Array.Empty<int>();
    }
}
