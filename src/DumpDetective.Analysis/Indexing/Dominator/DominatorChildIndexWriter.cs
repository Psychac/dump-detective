using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the dominator child index — <c>DominatorChildOffsets</c>/<c>DominatorChildAddresses</c>
/// (§10.4, Batch 2b, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): "what
/// would freeing this object free, one level down." Row-aligned with
/// <c>DominatorReachableAddresses</c>/<c>DominatorImmediateDominatorAddresses</c> — same convention,
/// no separate id scheme for a reader to remember.
///
/// Callers build <paramref name="childOffsetsByRow"/>/<paramref name="childAddressesByRow"/> using
/// the same row mapping <c>DominatorTreeIndexWriter.WriteImmediateDominatorAddresses</c> now takes
/// pre-computed, rather than each writer deriving its own — see
/// <c>DiskBackedObjectIndexWriter.BuildAndPersistDominatorTree</c>.
/// </summary>
internal static class DominatorChildIndexWriter
{
    /// <param name="childOffsetsByRow">
    /// Length = reachable-node count + 1. <c>childOffsetsByRow[i]..childOffsetsByRow[i+1]</c> is the
    /// slice of <paramref name="childAddressesByRow"/> holding row <c>i</c>'s dominator-tree
    /// children (folded leaves included as ordinary children, §10.5). Zero-width for a node with no
    /// dominator-tree children.
    /// </param>
    /// <param name="childAddressesByRow">Flat column of child addresses, grouped by parent row.</param>
    public static void Write(CacheContainerWriter containerWriter, int[] childOffsetsByRow, ulong[] childAddressesByRow)
    {
        containerWriter.BeginSection(CacheSectionId.DominatorChildOffsets);
        Span<byte> intBuf = stackalloc byte[4];
        foreach (int offset in childOffsetsByRow)
        {
            BinaryPrimitives.WriteInt32LittleEndian(intBuf, offset);
            containerWriter.Stream.Write(intBuf);
        }
        containerWriter.EndSection(childOffsetsByRow.Length);

        containerWriter.BeginSection(CacheSectionId.DominatorChildAddresses);
        Span<byte> ulongBuf = stackalloc byte[8];
        foreach (ulong childAddress in childAddressesByRow)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(ulongBuf, childAddress);
            containerWriter.Stream.Write(ulongBuf);
        }
        containerWriter.EndSection(childAddressesByRow.Length);
    }
}
