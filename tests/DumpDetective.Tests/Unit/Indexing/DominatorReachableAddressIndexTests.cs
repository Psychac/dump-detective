using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Round-trip tests for the <c>DominatorReachableAddresses</c> section — the Stage A-only
/// counterpart to <see cref="DominatorTreeIndexTests"/>'s §D7 pair (see
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §5).
/// </summary>
public class DominatorReachableAddressIndexTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorReachableAddressIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteContainer(params ulong[] sortedAddresses)
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);
        DominatorReachableAddressWriter.Write(writer, sortedAddresses);
        writer.Finish();
        return containerPath;
    }

    [Fact]
    public void Write_AddsSection()
    {
        string containerPath = WriteContainer(0x100UL, 0x200UL);

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.DominatorReachableAddresses).Should().BeTrue();
    }

    [Fact]
    public void Reader_IsReachable_TrueForEveryPersistedAddress()
    {
        string containerPath = WriteContainer(0x100UL, 0x200UL, 0x300UL);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorReachableAddressReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.IsReachable(0x100UL).Should().BeTrue();
            indexReader.IsReachable(0x200UL).Should().BeTrue();
            indexReader.IsReachable(0x300UL).Should().BeTrue();
        }
    }

    [Fact]
    public void Reader_UnknownAddress_ReturnsFalse()
    {
        string containerPath = WriteContainer(0x100UL);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorReachableAddressReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            indexReader!.IsReachable(0xDEADUL).Should().BeFalse();
        }
    }

    [Fact]
    public void Reader_LargeSortedSet_BinarySearchFindsEveryEntry()
    {
        const int count = 5000;
        var addresses = new ulong[count];
        for (int i = 0; i < count; i++)
            addresses[i] = (ulong)(i * 8 + 0x10000);

        string containerPath = WriteContainer(addresses);

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorReachableAddressReader.TryOpen(containerReader!, out var indexReader).Should().BeTrue();
        using (indexReader)
        {
            foreach (ulong address in addresses)
                indexReader!.IsReachable(address).Should().BeTrue();

            indexReader!.IsReachable(0x10000UL + count * 8).Should().BeFalse();
        }
    }
}
