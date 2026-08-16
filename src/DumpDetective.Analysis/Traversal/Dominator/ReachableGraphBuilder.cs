using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// ClrHeap-aware adapter around <see cref="ReachableGraphWalker"/> (design doc §D2/§D4/§D5/§D6):
/// resolves the root set, picks the edge source (persisted forward index, §D5, preferred; live
/// <c>ClrObject.EnumerateReferences</c> walk, §D4, fallback), runs the walk, then resolves each
/// node's <c>MethodTable</c>/<c>ShallowSize</c>/<see cref="GenerationTag"/>.
/// </summary>
internal static class ReachableGraphBuilder
{
    /// <summary>
    /// Builds the reachable graph, or returns <c>null</c> if the reachable population exceeded
    /// <paramref name="nodeCap"/> (§D6) — callers fall back to the existing top-K heuristic in
    /// that case, exactly like every other capped structure in this codebase.
    /// </summary>
    public static ReachableGraph? Build(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        int nodeCap,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
        var rootAddresses = new List<ulong>(roots.Count);
        foreach ((string _, ulong address) in roots)
        {
            if (address != 0)
                rootAddresses.Add(address);
        }

        Func<ulong, IEnumerable<ulong>> successors = BuildSuccessorsFunction(heap, cache);

        ReachableGraphWalkResult walkResult = ReachableGraphWalker.Walk(rootAddresses, successors, nodeCap, cancellationToken);
        if (walkResult.CapExceeded)
            return null;

        var methodTables = new ulong[walkResult.NodeCount];
        var shallowSizes = new ulong[walkResult.NodeCount];
        var generationTags = new GenerationTag[walkResult.NodeCount];

        for (int id = 0; id < walkResult.NodeCount; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong address = walkResult.Addresses[id];
            if (cache.TryGetObjectMetadata(heap, address, out ulong methodTable, out ulong size))
            {
                methodTables[id] = methodTable;
                shallowSizes[id] = size;
            }

            generationTags[id] = ResolveGenerationTag(heap, address);
        }

        return new ReachableGraph(walkResult, methodTables, shallowSizes, generationTags);
    }

    /// <summary>
    /// §D5: prefer the persisted forward-edge index (zero ClrMD calls) when available; fall back to
    /// a live scoped walk (§D4) otherwise — same graceful-degradation contract every other optional
    /// satellite index in this codebase already has.
    /// </summary>
    private static Func<ulong, IEnumerable<ulong>> BuildSuccessorsFunction(ClrHeap heap, IHeapAnalysisCache cache)
    {
        IForwardReferenceProvider? forwardIndex = cache.TryGetForwardIndexProvider();
        if (forwardIndex is not null)
        {
            return parent => forwardIndex.TryGetChildren(parent, out IReadOnlyList<ulong> children)
                ? children
                : Array.Empty<ulong>();
        }

        return parent => LiveSuccessors(heap, parent);
    }

    // §D4 fallback: the same ClrObject.EnumerateReferences(carefully: true) API this codebase's own
    // correct traversal (ObjectGraphTraversal.TryFindByPredicate) already uses — walks struct-typed
    // array elements and nested struct fields a manual field/array walk would miss.
    private static IEnumerable<ulong> LiveSuccessors(ClrHeap heap, ulong address)
    {
        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            yield break;

        foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
        {
            if (child.IsValid && child.Address != 0)
                yield return child.Address;
        }
    }

    private static GenerationTag ResolveGenerationTag(ClrHeap heap, ulong address)
    {
        ClrSegment? seg = heap.GetSegmentByAddress(address);
        if (seg is null)
            return GenerationTag.Unknown;

        switch (seg.Kind)
        {
            case GCSegmentKind.Large:
                return GenerationTag.Loh;
            case GCSegmentKind.Pinned:
                return GenerationTag.Poh;
            case GCSegmentKind.Frozen:
                return GenerationTag.Frozen;
            case GCSegmentKind.Generation2:
                return GenerationTag.Gen2;
            case GCSegmentKind.Generation1:
                return GenerationTag.Gen1;
            case GCSegmentKind.Generation0:
                return GenerationTag.Gen0;
            default:
                // Ephemeral (workstation GC) segment holds gen0/1/2 together — resolve per-object.
                try
                {
                    return seg.GetGeneration(address) switch
                    {
                        Generation.Generation0 => GenerationTag.Gen0,
                        Generation.Generation1 => GenerationTag.Gen1,
                        Generation.Generation2 => GenerationTag.Gen2,
                        _ => GenerationTag.Unknown,
                    };
                }
                catch
                {
                    return GenerationTag.Unknown;
                }
        }
    }
}
