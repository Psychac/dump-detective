using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.objects</c> capability's random-access surface, built
/// for <c>GCRootAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Delegates to <c>IHeapAnalysisCache.TryGetObjectMetadata</c> — the disk-backed point lookup every
/// pre-retyping analyzer needing an address's <c>(MethodTable, Size)</c> already used, falling back
/// to a live <c>heap.GetObject</c> resolution internally when the disk index is unavailable.
/// </summary>
internal sealed class HeapObjectLookup(ClrHeap heap, IHeapAnalysisCache cache) : IHeapObjectLookup
{
    public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
    {
        if (!cache.TryGetObjectMetadata(heap, address, out ulong methodTable, out ulong size))
        {
            heapObject = default;
            return false;
        }

        // Fallback name deliberately uses the SDK-wide "MT:0x{mt:x}" convention (matching
        // HeapTypeStatisticsQuery/HeapSegmentQuery) for a method table that fails to resolve to a
        // ClrType, not GCRootAnalyzer's own pre-retyping "0x{address:X}" fallback — narrow,
        // practically-unreachable divergence (a root's target method table failing to resolve at
        // all is not a case any reference dump has exercised), accepted for one shared lookup
        // surface's naming consistency rather than threading a per-caller fallback format through.
        string name = methodTable != 0
            ? heap.GetTypeByMethodTable(methodTable)?.Name ?? $"MT:0x{methodTable:x}"
            : "(unknown)";

        heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(name, methodTable == 0 ? null : methodTable), size, name);
        return true;
    }
}
