using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.references</c> capability, built for
/// <c>GCRootAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Wraps <c>ClrObject.EnumerateReferences(carefully: true)</c> directly — the same call
/// <c>BoundedGraphWalk</c> (this project's single canonical forward-BFS helper, see
/// docs/architecture.md § 7) already made at 18+ call sites pre-retyping — rather than any
/// disk-backed index, since forward references are always resolved live from field layout, never
/// persisted (see docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Graph rules).
/// </summary>
internal sealed class HeapReferenceQuery(ClrHeap heap) : IHeapReferenceQuery
{
    public IEnumerable<ulong> EnumerateReferences(ulong address)
    {
        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid)
            yield break;

        foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
        {
            if (child.IsValid && child.Address != 0)
                yield return child.Address;
        }
    }
}
