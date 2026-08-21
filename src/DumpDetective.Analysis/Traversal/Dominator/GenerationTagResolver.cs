using DumpDetective.Core.Enums;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Resolves a reachable node's <see cref="GenerationTag"/> (§D1 — report-filter only, never graph
/// membership). Formerly a method on <c>ReachableGraphBuilder</c>, the Phase 2 adapter that used to
/// build a <see cref="ReachableGraph"/> via its own live walk; that adapter's own <c>Build</c> method
/// is gone (§10.7, Batch 3, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md —
/// <c>DominatorAnalyzer</c> now reads the persisted tree instead of recomputing it), but this
/// resolver is purely live-ClrMD (<c>heap.GetSegmentByAddress</c>/<c>seg.GetGeneration</c>) with no
/// disk-cache dependency, so it still works identically wherever it's called from —
/// <c>DiskBackedObjectIndexWriter.Build</c>'s Stage B persistence (§10.4), its only caller now.
/// </summary>
internal static class GenerationTagResolver
{
    public static GenerationTag Resolve(ClrHeap heap, ulong address)
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
