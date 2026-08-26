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
            // Generation2 and the classic non-regions Ephemeral segment both hold SOH content.
            GCSegmentKind.Generation2 => HeapSegmentKind.SmallObjectHeap,
            GCSegmentKind.Ephemeral => HeapSegmentKind.SmallObjectHeap,
            // Generation0/Generation1 (regions-based GC) segments are ephemeral SOH too.
            GCSegmentKind.Generation0 => HeapSegmentKind.SmallObjectHeap,
            GCSegmentKind.Generation1 => HeapSegmentKind.SmallObjectHeap,
            // Anything else is a genuinely unrecognized segment kind — most likely a corrupted
            // dump or a newer ClrMD enum member this mapper hasn't been updated for. Do not
            // silently fold it into SOH; callers must handle HeapSegmentKind.Unknown explicitly.
            _ => HeapSegmentKind.Unknown,
        };
    }

    /// <summary>
    /// Maps to the per-generation <see cref="RegionGenerationKind"/>, preserving the Gen0/Gen1/Gen2
    /// split that <see cref="Map"/> collapses. <see cref="GCSegmentKind.Generation0"/> and
    /// <see cref="GCSegmentKind.Generation1"/> are only emitted for regions-based GC (.NET 8+); their
    /// presence in a dump's segment list is the signal that the heap uses regions.
    /// </summary>
    public static RegionGenerationKind MapRegionKind(ClrSegment segment)
    {
        return segment.Kind switch
        {
            GCSegmentKind.Generation0 => RegionGenerationKind.Generation0,
            GCSegmentKind.Generation1 => RegionGenerationKind.Generation1,
            GCSegmentKind.Generation2 => RegionGenerationKind.Generation2,
            GCSegmentKind.Large => RegionGenerationKind.Large,
            GCSegmentKind.Pinned => RegionGenerationKind.Pinned,
            GCSegmentKind.Frozen => RegionGenerationKind.Frozen,
            _ => RegionGenerationKind.Ephemeral,
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

    /// <summary>Returns the byte length of a <see cref="MemoryRange"/> (End − Start).</summary>
    public static ulong GetRangeLength(MemoryRange range) =>
        range.End >= range.Start ? range.End - range.Start : 0;

    /// <summary>Returns the committed bytes for the segment (length of committed memory range).</summary>
    public static ulong GetCommittedBytes(ClrSegment segment) =>
        GetRangeLength(segment.CommittedMemory);

    /// <summary>Returns the reserved bytes for the segment (length of reserved memory range).</summary>
    public static ulong GetReservedBytes(ClrSegment segment) =>
        GetRangeLength(segment.ReservedMemory);
}
