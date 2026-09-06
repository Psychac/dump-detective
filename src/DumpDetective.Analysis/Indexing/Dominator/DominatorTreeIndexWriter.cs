using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the two per-node scalar columns row-aligned with the already-written
/// <c>DominatorReachableAddresses</c> section — <c>DominatorImmediateDominatorAddresses</c> (§D7/§10.4,
/// row-index encoded since format v7, docs/cache/cache-format-clean-slate-redesign.md §4's aggressive
/// option) and <c>DominatorRetainedBytes</c> (§10.4, Batch 3) — mirroring the existing "Object index
/// (columnar)" pattern rather than a dense-id encoding, so <see cref="DominatorTreeIndexReader"/>
/// needs no dependency on any other section's id numbering.
///
/// Split from an earlier version of this class that also wrote <c>DominatorReachableAddresses</c>
/// itself — Stage A (§4/§7) already writes that section via <c>DominatorReachableAddressWriter</c>
/// at the point this class's write is now called from, so writing it a second time here would
/// duplicate the section in a write-once <see cref="CacheContainerWriter"/>. Wired into
/// <c>DiskBackedObjectIndexWriter.Build</c> since §10.1/§10.3/§10.4 — everything before that
/// (including Stage A) already runs before <see cref="CacheContainerWriter.Finish"/>, so no
/// container rewrite was ever actually needed once Stage A proved that premise wrong.
///
/// §10.4 Batch 2b: neither method sorts internally. The caller
/// (<c>DiskBackedObjectIndexWriter.BuildAndPersistDominatorTree</c>) computes each node's row in
/// <c>DominatorReachableAddresses</c>' sorted order once (<c>DominatorRowMapping</c>) and passes
/// values already placed into that row order. These methods just write the arrays.
///
/// No separate dominator child index is written any more — format v7 derives "what does this object
/// dominate" on demand by inverting <see cref="CacheSectionId.DominatorImmediateDominatorAddresses"/>
/// in memory (<see cref="DominatorChildIndexReader"/>), which is what makes the row-index encoding
/// here load-bearing rather than cosmetic: address-keyed values can't be inverted without a search
/// per row, row indices can.
/// </summary>
internal static class DominatorTreeIndexWriter
{
    /// <param name="dominatorRowByRow">
    /// Length must equal the reachable-node count. Entry <c>i</c> is the row (in the same
    /// <c>DominatorReachableAddresses</c> ordering, not an address) of node <c>i</c>'s immediate
    /// dominator, or <see cref="DominatorRowIndex.NoParentRow"/> for a direct child of the virtual
    /// root. The caller is responsible for the row ordering; this method trusts it and does not
    /// re-derive or validate it.
    /// </param>
    public static void WriteImmediateDominatorRows(CacheContainerWriter containerWriter, uint[] dominatorRowByRow)
    {
        containerWriter.BeginSection(CacheSectionId.DominatorImmediateDominatorAddresses);
        Span<byte> buf = stackalloc byte[sizeof(uint)];
        foreach (uint row in dominatorRowByRow)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, row);
            containerWriter.Stream.Write(buf);
        }
        containerWriter.EndSection(dominatorRowByRow.Length);
    }

    /// <param name="retainedBytesByRow">
    /// Length must equal the reachable-node count. Entry <c>i</c> is row <c>i</c>'s exact retained
    /// bytes (subtree sum, including its own shallow size; folded leaves get their own shallow size
    /// since as leaves their subtree is just themselves).
    /// </param>
    public static void WriteRetainedBytes(CacheContainerWriter containerWriter, ulong[] retainedBytesByRow)
    {
        containerWriter.BeginSection(CacheSectionId.DominatorRetainedBytes);
        Span<byte> buf = stackalloc byte[sizeof(ulong)];
        foreach (ulong value in retainedBytesByRow)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            containerWriter.Stream.Write(buf);
        }
        containerWriter.EndSection(retainedBytesByRow.Length);
    }
}
