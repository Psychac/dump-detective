using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the <c>DominatorReachableAddresses</c> section (§5,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): a sorted <c>ulong[]</c>
/// column of every object Stage A's reachability walk
/// (<see cref="Traversal.Dominator.ReachableGraphWalker"/>, <c>buildCsr: false</c>) found reachable
/// from a GC root.
///
/// Deliberately separate from <see cref="DominatorTreeIndexWriter"/>, which writes this same
/// section paired with <c>DominatorImmediateDominatorAddresses</c> — that's Stage B's shape
/// (needs `idom[]`, which Stage A's walk doesn't produce) and isn't wired into the main build yet.
/// Stage A has nothing to pair the addresses with, so it writes just this one column.
/// </summary>
internal static class DominatorReachableAddressWriter
{
    /// <param name="sortedAddresses">
    /// Must already be sorted ascending — <see cref="Traversal.Dominator.ReachableGraphWalkResult.ReachableAddresses"/>
    /// already is when <c>captureSortedAddresses: true</c>. Not re-sorted here; <see cref="DominatorReachableAddressReader"/>'s binary
    /// search depends on this.
    /// </param>
    /// <remarks>
    /// Format v6 stores a 4-byte delta from a per-block base rather than the full address
    /// (docs/cache/cache-format-clean-slate-redesign.md §10.4), so this writes three sections, not
    /// one. The column is small enough to encode in a single pass over the caller's list — no
    /// scratch files involved, unlike the object columns.
    /// </remarks>
    public static void Write(CacheContainerWriter containerWriter, IReadOnlyList<ulong> sortedAddresses)
    {
        List<ulong> blockBases = new(BlockDeltaColumn.BlockCountFor(sortedAddresses.Count));
        List<(uint RecordIndex, ulong Value)> overflow = [];

        containerWriter.BeginSection(CacheSectionId.DominatorReachableAddresses);
        uint addressChecksum = WriteDeltaColumn(containerWriter.Stream, sortedAddresses, blockBases, overflow);
        containerWriter.EndSection(sortedAddresses.Count, addressChecksum);

        containerWriter.BeginSection(CacheSectionId.DominatorReachableBlockBases);
        uint basesChecksum = BlockDeltaColumn.WriteBlockBases(containerWriter.Stream, blockBases, BlockBaseWriteBufferSize);
        containerWriter.EndSection(blockBases.Count, basesChecksum);

        containerWriter.BeginSection(CacheSectionId.DominatorReachableOverflow);
        uint overflowChecksum = ColumnOverflowTable.Write(containerWriter.Stream, overflow, BlockBaseWriteBufferSize);
        containerWriter.EndSection(overflow.Count, overflowChecksum);
    }

    /// <summary>
    /// Encodes and writes the delta column through a pooled buffer, hashing each filled chunk so the
    /// section closes with <see cref="CacheContainerWriter.EndSection(long, uint)"/> instead of the
    /// re-read overload. On the 27.5 GB dump this section is 233 MB and its re-read measured 28.2 s —
    /// paid as page-fault I/O, since it lands at the run's peak memory pressure
    /// (docs/cache/cache-redesign-runtime-rebalance.md §E.1). Buffering also collapses one
    /// <see cref="Stream.Write(ReadOnlySpan{byte})"/> and one <see cref="XxHash32.Append"/> per row —
    /// 58.3M of each on that dump — into one per 16K rows.
    /// </summary>
    private static uint WriteDeltaColumn(
        Stream stream,
        IReadOnlyList<ulong> sortedAddresses,
        List<ulong> blockBases,
        List<(uint RecordIndex, ulong Value)> overflow)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockBaseWriteBufferSize);

        try
        {
            int offset = 0;
            for (int row = 0; row < sortedAddresses.Count; row++)
            {
                uint delta = BlockDeltaColumn.Encode(sortedAddresses[row], row, blockBases, overflow);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), delta);
                offset += BlockDeltaColumn.DeltaWidth;

                if (offset + BlockDeltaColumn.DeltaWidth > buffer.Length)
                {
                    stream.Write(buffer, 0, offset);
                    hasher.Append(buffer.AsSpan(0, offset));
                    offset = 0;
                }
            }

            if (offset > 0)
            {
                stream.Write(buffer, 0, offset);
                hasher.Append(buffer.AsSpan(0, offset));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    private const int BlockBaseWriteBufferSize = 64 * 1024;
}
