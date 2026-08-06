using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Centralises mapping and classification logic for <see cref="ClrSegment"/> kinds.
/// Other analyzers should prefer this to ad-hoc string-based checks.
/// </summary>
internal static class SegmentKindMapper
{
    public static HeapSegmentKind Map(ClrSegment segment)
    {
        return segment.Kind switch
        {
            GCSegmentKind.Large => HeapSegmentKind.LargeObjectHeap,
            GCSegmentKind.Pinned => HeapSegmentKind.PinnedObjectHeap,
            GCSegmentKind.Frozen => HeapSegmentKind.Frozen,
            _ => HeapSegmentKind.SmallObjectHeap,
        };
    }

    public static bool IsEphemeral(ClrSegment segment)
    {
        return segment.Kind switch
        {
            GCSegmentKind.Generation0 or GCSegmentKind.Generation1 or GCSegmentKind.Ephemeral => true,
            GCSegmentKind.Generation2 or GCSegmentKind.Large or GCSegmentKind.Pinned or GCSegmentKind.Frozen => false,
            _ => segment.Generation0.Length > 0,
        };
    }

    /// <summary>
    /// Resolves the GC generation (0/1/2) of the object at <paramref name="address"/>.
    /// Returns -1 if the address is unresolvable (invalid address, no owning segment, or the
    /// underlying ClrMD lookup throws).
    /// </summary>
    public static int ResolveGeneration(ClrHeap heap, ulong address)
    {
        if (address == 0) return -1;
        ClrSegment? seg = heap.GetSegmentByAddress(address);
        if (seg is null) return -1;
        try { return (int)seg.GetGeneration(address); }
        catch { return -1; }
    }
}
