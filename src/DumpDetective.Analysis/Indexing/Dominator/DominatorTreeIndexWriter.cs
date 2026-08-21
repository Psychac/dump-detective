using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the two per-node scalar columns row-aligned with the already-written
/// <c>DominatorReachableAddresses</c> section — <c>DominatorImmediateDominatorAddresses</c> (§D7/§10.4)
/// and <c>DominatorRetainedBytes</c> (§10.4, Batch 3) — mirroring the existing "Object index
/// (columnar)" pattern rather than a dense-id encoding, so <see cref="DominatorTreeIndexReader"/>
/// needs no dependency on any other section's id numbering. Kept together in one writer since both
/// are the same shape (one scalar per row) and always computed by the same caller in the same pass;
/// the dominator child index (variable-length CSR, not a fixed scalar) is structurally different and
/// stays in its own <see cref="DominatorChildIndexWriter"/>.
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
/// <c>DominatorReachableAddresses</c>' sorted order once (<c>DominatorRowMapping</c>) — shared with
/// the dominator child index instead of each writer re-deriving that order separately — and passes
/// values already placed into that row order. These methods just write the arrays.
/// </summary>
internal static class DominatorTreeIndexWriter
{
    /// <param name="dominatorAddressesByRow">
    /// Length must equal the reachable-node count. Entry <c>i</c> is the immediate-dominator address
    /// of the node at row <c>i</c> in the already-written <c>DominatorReachableAddresses</c> section
    /// (<c>0</c> for a direct child of the virtual root — no real dominator address). The caller is
    /// responsible for the row ordering; this method trusts it and does not re-derive or validate it.
    /// </param>
    public static void WriteImmediateDominatorAddresses(CacheContainerWriter containerWriter, ulong[] dominatorAddressesByRow)
    {
        WriteRowAlignedColumn(containerWriter, CacheSectionId.DominatorImmediateDominatorAddresses, dominatorAddressesByRow);
    }

    /// <param name="retainedBytesByRow">
    /// Length must equal the reachable-node count. Entry <c>i</c> is row <c>i</c>'s exact retained
    /// bytes (subtree sum, including its own shallow size; folded leaves get their own shallow size
    /// since as leaves their subtree is just themselves).
    /// </param>
    public static void WriteRetainedBytes(CacheContainerWriter containerWriter, ulong[] retainedBytesByRow)
    {
        WriteRowAlignedColumn(containerWriter, CacheSectionId.DominatorRetainedBytes, retainedBytesByRow);
    }

    private static void WriteRowAlignedColumn(CacheContainerWriter containerWriter, CacheSectionId sectionId, ulong[] valuesByRow)
    {
        containerWriter.BeginSection(sectionId);
        Span<byte> buf = stackalloc byte[8];
        foreach (ulong value in valuesByRow)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            containerWriter.Stream.Write(buf);
        }
        containerWriter.EndSection(valuesByRow.Length);
    }
}
