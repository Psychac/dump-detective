using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Round-trip tests for §D7's persisted-tree section format — the format layer only (a standalone
/// container built fresh in-test), not the "append to an already-finalized cache.bin" integration,
/// which is still an open design question (see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md Open Questions).
/// </summary>
public class DominatorTreeIndexTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorTreeIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteContainer(params (ulong Address, ulong DominatorAddress)[] entries)
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);
        DominatorTreeIndexWriter.Write(writer, entries);
        writer.Finish();
        return containerPath;
    }

    [Fact]
    public void Write_AddsBothColumnarSections()
    {
        string containerPath = WriteContainer((0x100UL, 0x1UL), (0x200UL, 0x1UL));

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.DominatorReachableAddresses).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.DominatorImmediateDominatorAddresses).Should().BeTrue();
    }

    [Fact]
    public void Reader_TryGetImmediateDominator_RoundTripsExactly()
    {
        // Deliberately unsorted input — the writer must sort before persisting.
        var entries = new (ulong Address, ulong DominatorAddress)[]
        {
            (0x300UL, 0x200UL),
            (0x100UL, 0x1UL),   // direct child of the virtual root (its "dominator" here is the real GC root)
            (0x200UL, 0x1UL),
        };
        string containerPath = WriteContainer(entries);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetImmediateDominator(0x100UL, out ulong dom1).Should().BeTrue();
            dom1.Should().Be(0x1UL);

            indexReader.TryGetImmediateDominator(0x200UL, out ulong dom2).Should().BeTrue();
            dom2.Should().Be(0x1UL);

            indexReader.TryGetImmediateDominator(0x300UL, out ulong dom3).Should().BeTrue();
            dom3.Should().Be(0x200UL);
        }
    }

    [Fact]
    public void Reader_UnknownAddress_ReturnsFalse()
    {
        string containerPath = WriteContainer((0x100UL, 0x1UL));

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetImmediateDominator(0xDEADUL, out ulong dominatorAddress).Should().BeFalse();
            dominatorAddress.Should().Be(0);
        }
    }

    [Fact]
    public void Reader_LargeSortedSet_BinarySearchFindsEveryEntry()
    {
        const int count = 10_000;
        var entries = new (ulong Address, ulong DominatorAddress)[count];
        for (int i = 0; i < count; i++)
            entries[i] = ((ulong)(i * 8 + 0x10000), (ulong)(i / 2 * 8 + 0x10000));

        string containerPath = WriteContainer(entries);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            foreach ((ulong address, ulong expectedDominator) in entries)
            {
                indexReader!.TryGetImmediateDominator(address, out ulong actual).Should().BeTrue();
                actual.Should().Be(expectedDominator);
            }
        }
    }
}
