using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// The reachable graph plus per-node metadata needed downstream (§Architecture step 2-3) — output
/// of <see cref="ReachableGraphBuilder.Build"/>, input to <see cref="LeafFolder"/> and eventually
/// Lengauer-Tarjan.
/// </summary>
internal sealed class ReachableGraph
{
    public int NodeCount { get; }
    public ulong[] Addresses { get; }          // id -> Address
    public ulong[] MethodTables { get; }       // id -> MethodTable
    public ulong[] ShallowSizes { get; }       // id -> ShallowSize
    public GenerationTag[] GenerationTags { get; } // id -> report-filter tag (§D1 — never graph membership)
    public bool[] IsRoot { get; }
    public int[] OutDegree { get; }
    public int[] InDegree { get; }
    public int[] FwdOffsets { get; }
    public int[] FwdTargets { get; }
    public int[] RevOffsets { get; }
    public int[] RevTargets { get; }

    public ReachableGraph(
        ReachableGraphWalkResult walkResult,
        ulong[] methodTables,
        ulong[] shallowSizes,
        GenerationTag[] generationTags)
    {
        NodeCount = walkResult.NodeCount;
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
}
