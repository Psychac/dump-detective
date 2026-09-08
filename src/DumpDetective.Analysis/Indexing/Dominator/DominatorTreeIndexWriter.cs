using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

using DumpDetective.Analysis.Indexing.Columns;
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
    /// <remarks>
    /// Narrowed to the cheapest width the distribution allows, exactly like <c>ObjectSizes</c>:
    /// values that don't fit store an all-ones sentinel and escape to
    /// <see cref="CacheSectionId.DominatorRetainedBytesOverflow"/>. 69–75% of rows are
    /// dominator-tree leaves whose retained bytes are just their own shallow size, so both reference
    /// dumps pick 2 bytes at a 0.10–0.19% escape rate — 445.10 MiB to 112.51 MiB on the 27.5 GB dump
    /// (docs/cache/cache-ideal-design.md §7.1).
    ///
    /// Storing <c>retained - ownSize</c> so leaf rows become zero was measured and rejected: it
    /// costs 112.56 MiB against 112.51 for the raw column, because the leaves are small in absolute
    /// terms, not just relative to themselves. Narrowing alone gets the whole saving.
    /// </remarks>
    public static void WriteRetainedBytes(CacheContainerWriter containerWriter, ulong[] retainedBytesByRow)
    {
        long escapesAtTwoBytes = 0;
        long escapesAtFourBytes = 0;
        foreach (ulong value in retainedBytesByRow)
        {
            if (value >= ushort.MaxValue) escapesAtTwoBytes++;
            if (value >= uint.MaxValue) escapesAtFourBytes++;
        }

        int width = NarrowColumnWidth.Choose(retainedBytesByRow.Length, escapesAtTwoBytes, escapesAtFourBytes);
        var overflow = new List<(uint RecordIndex, ulong Value)>(
            capacity: (int)Math.Min(int.MaxValue, width == sizeof(ushort) ? escapesAtTwoBytes : width == sizeof(uint) ? escapesAtFourBytes : 0));

        containerWriter.BeginSection(CacheSectionId.DominatorRetainedBytes);

        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
        try
        {
            int offset = 0;
            for (int row = 0; row < retainedBytesByRow.Length; row++)
            {
                ulong value = retainedBytesByRow[row];
                Span<byte> slot = buffer.AsSpan(offset);

                switch (width)
                {
                    case sizeof(ushort):
                        if (value >= ushort.MaxValue)
                        {
                            overflow.Add(((uint)row, value));
                            BinaryPrimitives.WriteUInt16LittleEndian(slot, ushort.MaxValue);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)value);
                        }
                        break;
                    case sizeof(uint):
                        if (value >= uint.MaxValue)
                        {
                            overflow.Add(((uint)row, value));
                            BinaryPrimitives.WriteUInt32LittleEndian(slot, uint.MaxValue);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)value);
                        }
                        break;
                    default:
                        BinaryPrimitives.WriteUInt64LittleEndian(slot, value);
                        break;
                }

                offset += width;

                if (offset + width > buffer.Length)
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

        // Written even when empty, so a reader can distinguish "narrowed with no escapes" from
        // "narrowed and the escape table failed to write" — same contract ObjectSizeOverflow has.
        if (width != NarrowColumnWidth.Full)
        {
            containerWriter.BeginSection(CacheSectionId.DominatorRetainedBytesOverflow);
            uint overflowChecksum = ColumnOverflowTable.Write(containerWriter.Stream, overflow, WriteBufferSize);
            containerWriter.EndSection(overflow.Count, overflowChecksum);
        }
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
