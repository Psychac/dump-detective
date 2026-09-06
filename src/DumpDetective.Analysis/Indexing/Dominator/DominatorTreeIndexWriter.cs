using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

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

        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
        try
        {
            int offset = 0;
            foreach (uint row in dominatorRowByRow)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), row);
                offset += sizeof(uint);

                if (offset + sizeof(uint) > buffer.Length)
                    FlushChunk(containerWriter.Stream, hasher, buffer, ref offset);
            }

            if (offset > 0)
                FlushChunk(containerWriter.Stream, hasher, buffer, ref offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        containerWriter.EndSection(dominatorRowByRow.Length, hasher.GetCurrentHashAsUInt32());
    }

    /// <param name="retainedBytesByRow">
    /// Length must equal the reachable-node count. Entry <c>i</c> is row <c>i</c>'s exact retained
    /// bytes (subtree sum, including its own shallow size; folded leaves get their own shallow size
    /// since as leaves their subtree is just themselves).
    /// </param>
    public static void WriteRetainedBytes(CacheContainerWriter containerWriter, ulong[] retainedBytesByRow)
    {
        containerWriter.BeginSection(CacheSectionId.DominatorRetainedBytes);

        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
        try
        {
            int offset = 0;
            foreach (ulong value in retainedBytesByRow)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);
                offset += sizeof(ulong);

                if (offset + sizeof(ulong) > buffer.Length)
                    FlushChunk(containerWriter.Stream, hasher, buffer, ref offset);
            }

            if (offset > 0)
                FlushChunk(containerWriter.Stream, hasher, buffer, ref offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        containerWriter.EndSection(retainedBytesByRow.Length, hasher.GetCurrentHashAsUInt32());
    }

    /// <summary>
    /// Writes the filled prefix of <paramref name="buffer"/> and folds it into
    /// <paramref name="hasher"/>, so both columns close via
    /// <see cref="CacheContainerWriter.EndSection(long, uint)"/> rather than that class's re-read
    /// overload. On the 27.5 GB dump these two sections are 233 MB and 467 MB and their re-reads
    /// measured 3.7 s and 4.0 s (docs/cache/cache-redesign-runtime-rebalance.md §E.1), on top of one
    /// stream write and one hash append per row — 58.3M of each, per column — that the buffer removes.
    /// </summary>
    private static void FlushChunk(Stream stream, XxHash32 hasher, byte[] buffer, ref int offset)
    {
        stream.Write(buffer, 0, offset);
        hasher.Append(buffer.AsSpan(0, offset));
        offset = 0;
    }

    private const int WriteBufferSize = 64 * 1024;
}
