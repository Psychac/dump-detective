using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Round-trip tests for the <c>DominatorTreeMetadata</c> section (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md).
/// </summary>
public class DominatorTreeMetadataTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorTreeMetadataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTotalAndPerMethodTableRollup()
    {
        var rollup = new DominatorRetainedBytesRollupResult(
            totalRetainedBytes: 12345UL,
            retainedBytesByMethodTable: new Dictionary<ulong, ulong> { [0xAAUL] = 100UL, [0xBBUL] = 200UL });

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorTreeMetadataWriter.Write(writer, rollup);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorTreeMetadataReader.TryOpen(containerReader!, out DominatorTreeMetadata? metadata).Should().BeTrue();

        metadata!.TotalRetainedBytes.Should().Be(12345UL);
        metadata.ByMethodTable.Should().BeEquivalentTo(
        [
            new DominatorTypeRetainedBytes { MethodTable = 0xAAUL, RetainedBytes = 100UL },
            new DominatorTypeRetainedBytes { MethodTable = 0xBBUL, RetainedBytes = 200UL },
        ]);
    }

    [Fact]
    public void Write_EmptyRollup_RoundTripsWithZeroTotalAndEmptyList()
    {
        var rollup = new DominatorRetainedBytesRollupResult(
            totalRetainedBytes: 0UL, retainedBytesByMethodTable: new Dictionary<ulong, ulong>());

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorTreeMetadataWriter.Write(writer, rollup);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorTreeMetadataReader.TryOpen(containerReader!, out DominatorTreeMetadata? metadata).Should().BeTrue();

        metadata!.TotalRetainedBytes.Should().Be(0UL);
        metadata.ByMethodTable.Should().BeEmpty();
    }

    [Fact]
    public void TryOpen_MissingSection_ReturnsFalse()
    {
        string containerPath = Path.Combine(_tempDir, "cache-empty.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorTreeMetadataReader.TryOpen(containerReader!, out DominatorTreeMetadata? metadata).Should().BeFalse();
        metadata.Should().BeNull();
    }
}
