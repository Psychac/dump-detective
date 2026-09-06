using System.Buffers.Binary;

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

        Span<byte> buf = stackalloc byte[BlockDeltaColumn.DeltaWidth];
        for (int row = 0; row < sortedAddresses.Count; row++)
        {
            uint delta = BlockDeltaColumn.Encode(sortedAddresses[row], row, blockBases, overflow);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, delta);
            containerWriter.Stream.Write(buf);
        }

        containerWriter.EndSection(sortedAddresses.Count);

        containerWriter.BeginSection(CacheSectionId.DominatorReachableBlockBases);
        uint basesChecksum = BlockDeltaColumn.WriteBlockBases(containerWriter.Stream, blockBases, BlockBaseWriteBufferSize);
        containerWriter.EndSection(blockBases.Count, basesChecksum);

        containerWriter.BeginSection(CacheSectionId.DominatorReachableOverflow);
        uint overflowChecksum = ColumnOverflowTable.Write(containerWriter.Stream, overflow, BlockBaseWriteBufferSize);
        containerWriter.EndSection(overflow.Count, overflowChecksum);
    }

    private const int BlockBaseWriteBufferSize = 64 * 1024;
}
