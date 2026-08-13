using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Shared "pick top-N, run BFS" targeting layer in front of
/// <see cref="BoundedGraphWalk.ComputeExclusiveRetained"/>. Skips the BFS entirely for
/// candidates whose shallow size already IS their retained size (arrays/strings/blobs with
/// no reference-typed field anywhere in the field tree — see
/// <see cref="IHeapAnalysisCache.MethodTableHasOutgoingRefs"/>'s recursive
/// <c>TypeMetadataCache.ContainsPointers</c> check underneath), and caps BFS spend to the top
/// <c>maxCandidatesToWalk</c> survivors by shallow size, independent of how many candidates
/// pass the shape filter.
/// </summary>
internal static class RetainedSizeCandidateSelector
{
    /// <summary>
    /// O(1), no BFS, no live heap read beyond the cached type lookup. True when this type's
    /// shape means shallow size is not already an accurate proxy for retained size (i.e. it
    /// has at least one reference-typed field somewhere in its field tree, per
    /// <see cref="IHeapAnalysisCache.MethodTableHasOutgoingRefs"/>'s recursive check).
    /// </summary>
    public static bool RequiresWalk(IHeapAnalysisCache cache, ClrHeap heap, ulong methodTable)
    {
        if (methodTable == 0)
            return false;

        return cache.MethodTableHasOutgoingRefs(heap, methodTable);
    }

    /// <summary>
    /// Ranks <paramref name="candidates"/> by shallow size descending, applies the Step-1 shape
    /// filter, then walks only the top <paramref name="maxCandidatesToWalk"/> survivors via
    /// <see cref="BoundedGraphWalk.ComputeExclusiveRetained"/> against the shared
    /// <paramref name="visited"/> set. Candidates that don't require a walk (or that don't
    /// survive the budget cut) are returned with <c>RetainedSize == ShallowSize</c> and
    /// <c>WasWalked == false</c> — never silently dropped, so caller output shape/counts stay
    /// consistent with the input candidate list.
    /// </summary>
    public static IReadOnlyList<RetainedSizeResult> SelectAndCompute(
        IReadOnlyList<(ulong Address, ulong MethodTable, ulong ShallowSize)> candidates,
        ClrHeap heap,
        IHeapAnalysisCache cache,
        HashSet<ulong> visited,
        int maxCandidatesToWalk,
        int maxBreadth = 10_000,
        int maxDepth = 20,
        CancellationToken cancellationToken = default)
    {
        var results = new RetainedSizeResult[candidates.Count];

        // Stable order by descending shallow size; ties keep original relative order.
        var order = new int[candidates.Count];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;

        Array.Sort(order, (a, b) =>
        {
            int cmp = candidates[b].ShallowSize.CompareTo(candidates[a].ShallowSize);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        int walked = 0;
        for (int rank = 0; rank < order.Length; rank++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int i = order[rank];
            (ulong address, ulong methodTable, ulong shallowSize) = candidates[i];

            bool eligible = walked < maxCandidatesToWalk && RequiresWalk(cache, heap, methodTable);
            if (!eligible)
            {
                results[i] = new RetainedSizeResult(address, shallowSize, shallowSize, WasWalked: false);
                continue;
            }

            ClrObject obj = heap.GetObject(address);
            ulong retainedSize = BoundedGraphWalk.ComputeExclusiveRetained(obj, heap, visited, maxBreadth, maxDepth);
            walked++;

            // A lower-than-shallow (even 0) result is legitimate: the address was already
            // claimed by an earlier candidate's walk in this batch (shared visited-set
            // exclusivity), not a walk failure. Surfaced as computed — no clamping — per
            // docs/analysis/retained-size-candidate-selection.md open question 2.
            results[i] = new RetainedSizeResult(address, shallowSize, retainedSize, WasWalked: true);
        }

        return results;
    }
}

internal readonly record struct RetainedSizeResult(
    ulong Address,
    ulong ShallowSize,
    ulong RetainedSize,
    bool WasWalked);
