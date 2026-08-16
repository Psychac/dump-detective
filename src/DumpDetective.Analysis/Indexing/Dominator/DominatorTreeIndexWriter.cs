using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the §D7 persisted-tree format
/// (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md): two parallel columnar
/// <c>ulong[]</c> sections — reachable-node addresses (sorted) and each node's immediate-dominator
/// address (aligned by index) — mirroring the existing "Object index (columnar)" pattern rather
/// than a dense-id encoding, so a reader needs no dependency on any other section's id numbering.
///
/// <b>Not yet wired into the main build.</b> <see cref="CacheContainerWriter"/> is write-once
/// (always creates a new file on <see cref="CacheContainerWriter.Finish"/>) — persisting this
/// section, computed by <c>DominatorAnalyzer</c> in Phase 2 after Phase 1's container write has
/// already finished, requires either a full container rewrite (copying the entire existing
/// cache.bin, which can be multi-GB) or a design change neither of which is decided yet. This
/// class implements and validates the on-disk *format* only, via a standalone container built
/// fresh in tests — not the "append to an already-finalized cache.bin" integration.
/// </summary>
internal static class DominatorTreeIndexWriter
{
    /// <param name="entries">
    /// One entry per reachable node: its own address and its immediate dominator's address. Does
    /// not need to be pre-sorted — this method sorts by address before writing.
    /// </param>
    public static void Write(CacheContainerWriter containerWriter, IReadOnlyList<(ulong Address, ulong DominatorAddress)> entries)
    {
        var sorted = entries.ToArray();
        Array.Sort(sorted, static (a, b) => a.Address.CompareTo(b.Address));

        containerWriter.BeginSection(CacheSectionId.DominatorReachableAddresses);
        foreach ((ulong address, _) in sorted)
        {
            Span<byte> buf = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, address);
            containerWriter.Stream.Write(buf);
        }
        containerWriter.EndSection(sorted.Length);

        containerWriter.BeginSection(CacheSectionId.DominatorImmediateDominatorAddresses);
        foreach ((_, ulong dominatorAddress) in sorted)
        {
            Span<byte> buf = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, dominatorAddress);
            containerWriter.Stream.Write(buf);
        }
        containerWriter.EndSection(sorted.Length);
    }
}
