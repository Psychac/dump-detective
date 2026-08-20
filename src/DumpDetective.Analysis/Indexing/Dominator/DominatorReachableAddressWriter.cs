using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the <c>DominatorReachableAddresses</c> section (§5,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): a sorted <c>ulong[]</c>
/// column of every object Stage A's reachability walk
/// (<see cref="Traversal.Dominator.IncrementalReachableWalker"/>) found reachable from a GC root.
///
/// Deliberately separate from <see cref="DominatorTreeIndexWriter"/>, which writes this same
/// section paired with <c>DominatorImmediateDominatorAddresses</c> — that's Stage B's shape
/// (needs `idom[]`, which Stage A's walk doesn't produce) and isn't wired into the main build yet.
/// Stage A has nothing to pair the addresses with, so it writes just this one column.
/// </summary>
internal static class DominatorReachableAddressWriter
{
    /// <param name="sortedAddresses">
    /// Must already be sorted ascending — <see cref="Traversal.Dominator.IncrementalReachableWalker.Result.ReachableAddresses"/>
    /// already is. Not re-sorted here; <see cref="DominatorReachableAddressReader"/>'s binary
    /// search depends on this.
    /// </param>
    public static void Write(CacheContainerWriter containerWriter, IReadOnlyList<ulong> sortedAddresses)
    {
        containerWriter.BeginSection(CacheSectionId.DominatorReachableAddresses);

        Span<byte> buf = stackalloc byte[8];
        foreach (ulong address in sortedAddresses)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, address);
            containerWriter.Stream.Write(buf);
        }

        containerWriter.EndSection(sortedAddresses.Count);
    }
}
