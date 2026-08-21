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

    private string WriteContainer(
        (ulong Address, ulong DominatorAddress)[] entries, Dictionary<ulong, ulong>? retainedBytesByAddress = null)
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);

        // §10.4: DominatorReachableAddresses and DominatorImmediateDominatorAddresses are now
        // written by two separate classes (Stage A owns the former) — both are written here so
        // these round-trip tests still exercise DominatorTreeIndexReader's real two-section contract.
        // §10.4 Batch 2b: WriteImmediateDominatorAddresses now takes a row-ordered array directly
        // (the caller — normally DiskBackedObjectIndexWriter.BuildAndPersistDominatorTree — computes
        // the row order once and reuses it for the child index too) rather than sorting tuples
        // itself, so this test builds that row order the same way a real caller would.
        var byAddress = entries.ToDictionary(e => e.Address, e => e.DominatorAddress);
        var sortedAddresses = byAddress.Keys.OrderBy(a => a).ToArray();
        var dominatorAddressesByRow = sortedAddresses.Select(a => byAddress[a]).ToArray();
        DominatorReachableAddressWriter.Write(writer, sortedAddresses);
        DominatorTreeIndexWriter.WriteImmediateDominatorAddresses(writer, dominatorAddressesByRow);

        // §10.4 Batch 3: DominatorRetainedBytes is optional in this test on purpose — omitting it
        // exercises DominatorTreeIndexReader's backward-compatibility path for a cache.bin written
        // before this column existed (Batch 2a/2b).
        if (retainedBytesByAddress is not null)
        {
            var retainedBytesByRow = sortedAddresses.Select(a => retainedBytesByAddress[a]).ToArray();
            DominatorTreeIndexWriter.WriteRetainedBytes(writer, retainedBytesByRow);
        }

        writer.Finish();
        return containerPath;
    }

    private string WriteContainer(params (ulong Address, ulong DominatorAddress)[] entries) => WriteContainer(entries, retainedBytesByAddress: null);

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

    [Fact]
    public void Reader_TryGetRetainedBytes_RoundTripsExactly()
    {
        (ulong Address, ulong DominatorAddress)[] entries =
        [
            (0x100UL, 0x0UL),
            (0x200UL, 0x100UL),
        ];
        var retainedBytes = new Dictionary<ulong, ulong> { [0x100UL] = 300UL, [0x200UL] = 200UL };

        string containerPath = WriteContainer(entries, retainedBytes);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetRetainedBytes(0x100UL, out ulong retained1).Should().BeTrue();
            retained1.Should().Be(300UL);

            indexReader.TryGetRetainedBytes(0x200UL, out ulong retained2).Should().BeTrue();
            retained2.Should().Be(200UL);
        }
    }

    [Fact]
    public void Reader_TryGetRetainedBytes_MissingSection_ReturnsFalseWithoutBreakingIdomReads()
    {
        // Legacy cache.bin written before DominatorRetainedBytes existed (Batch 2a/2b) — must not
        // be mistaken for a corrupt container, and idom reads must still work.
        string containerPath = WriteContainer((0x100UL, 0x1UL));

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetRetainedBytes(0x100UL, out ulong retainedBytes).Should().BeFalse();
            retainedBytes.Should().Be(0);

            indexReader.TryGetImmediateDominator(0x100UL, out ulong dominatorAddress).Should().BeTrue();
            dominatorAddress.Should().Be(0x1UL);
        }
    }

    [Fact]
    public void Reader_TryGetRetainedBytes_UnknownAddress_ReturnsFalse()
    {
        var retainedBytes = new Dictionary<ulong, ulong> { [0x100UL] = 50UL };
        string containerPath = WriteContainer([(0x100UL, 0x0UL)], retainedBytes);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeIndexReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.TryGetRetainedBytes(0xDEADUL, out ulong retained).Should().BeFalse();
            retained.Should().Be(0);
        }
    }
}
