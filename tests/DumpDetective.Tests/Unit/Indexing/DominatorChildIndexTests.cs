using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Round-trip tests for the dominator child index's on-disk format (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — the format layer only,
/// mirroring <c>DominatorTreeIndexTests</c>'s style. The algorithm that builds
/// <c>childOffsetsByRow</c>/<c>childAddressesByRow</c> is tested separately in
/// <c>DominatorChildIndexBuilderTests</c>; this file only exercises what
/// <see cref="DominatorChildIndexWriter"/>/<see cref="DominatorChildIndexReader"/> do with
/// already-built arrays.
/// </summary>
public class DominatorChildIndexTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorChildIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteContainer(ulong[] sortedAddresses, int[] childOffsetsByRow, ulong[] childAddressesByRow)
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);

        DominatorReachableAddressWriter.Write(writer, sortedAddresses);
        DominatorChildIndexWriter.Write(writer, childOffsetsByRow, childAddressesByRow);

        writer.Finish();
        return containerPath;
    }

    [Fact]
    public void TryGetChildren_RoundTripsExactly()
    {
        // Row order: 0x100, 0x200, 0x300, 0x400. 0x100 -> [0x200, 0x300]; 0x300 -> [0x400].
        ulong[] sortedAddresses = [0x100UL, 0x200UL, 0x300UL, 0x400UL];
        int[] childOffsets = [0, 2, 2, 3, 3];
        ulong[] childAddresses = [0x200UL, 0x300UL, 0x400UL];

        string containerPath = WriteContainer(sortedAddresses, childOffsets, childAddresses);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeTrue();
        using (reader)
        {
            reader!.TryGetChildren(0x100UL, out ulong[] childrenOf100).Should().BeTrue();
            childrenOf100.Should().Equal([0x200UL, 0x300UL]);

            reader.TryGetChildren(0x300UL, out ulong[] childrenOf300).Should().BeTrue();
            childrenOf300.Should().Equal([0x400UL]);
        }
    }

    [Fact]
    public void TryGetChildren_NodeWithNoChildren_ReturnsTrueWithEmptyArray()
    {
        ulong[] sortedAddresses = [0x100UL, 0x200UL];
        int[] childOffsets = [0, 1, 1];
        ulong[] childAddresses = [0x200UL];

        string containerPath = WriteContainer(sortedAddresses, childOffsets, childAddresses);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeTrue();
        using (reader)
        {
            reader!.TryGetChildren(0x200UL, out ulong[] children).Should().BeTrue();
            children.Should().BeEmpty();
        }
    }

    [Fact]
    public void TryGetChildren_UnknownAddress_ReturnsFalse()
    {
        ulong[] sortedAddresses = [0x100UL];
        int[] childOffsets = [0, 0];
        ulong[] childAddresses = [];

        string containerPath = WriteContainer(sortedAddresses, childOffsets, childAddresses);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeTrue();
        using (reader)
        {
            reader!.TryGetChildren(0xDEADUL, out ulong[] children).Should().BeFalse();
            children.Should().BeEmpty();
        }
    }

    [Fact]
    public void TryOpen_NoDominatorTreeEdgesAtAll_ChildAddressesSectionEmpty_StillOpens()
    {
        // Legitimate zero-total-children case (§10.4's DominatorChildIndexReader field comment) —
        // must not be mistaken for a missing/corrupt section.
        ulong[] sortedAddresses = [0x100UL, 0x200UL];
        int[] childOffsets = [0, 0, 0];
        ulong[] childAddresses = [];

        string containerPath = WriteContainer(sortedAddresses, childOffsets, childAddresses);

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeTrue();
        using (reader)
        {
            reader!.TryGetChildren(0x100UL, out ulong[] children).Should().BeTrue();
            children.Should().BeEmpty();
        }
    }

    [Fact]
    public void TryOpen_MissingChildOffsetsSection_ReturnsFalse()
    {
        string containerPath = Path.Combine(_tempDir, "cache-partial.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorReachableAddressWriter.Write(writer, [0x100UL]);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeFalse();
        reader.Should().BeNull();
    }
}
