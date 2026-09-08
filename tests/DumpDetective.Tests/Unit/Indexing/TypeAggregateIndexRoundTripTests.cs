using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// P2-3: <see cref="TypeAggregateIndexEntry.Gen2TotalSize"/> must round-trip through
/// <see cref="TypeAggregateIndexWriter"/>/<see cref="TypeAggregateIndexReader"/> (TypeEntrySize
/// grew from 80 to 88 bytes, format Version bumped 3 -> 4).
/// </summary>
public sealed class TypeAggregateIndexRoundTripTests : IDisposable
{
    private readonly string _testDir;

    public TypeAggregateIndexRoundTripTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "type-aggregate-roundtrip-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void WriteAndRead_Gen2TotalSize_RoundTripsExactly()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");

        var typeAggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            [0x1000] = new TypeAggregateIndexEntry(
                MethodTable: 0x1000, ModuleId: 5, Count: 100, TotalSize: 50_000, LohCount: 0, LohSize: 0,
                SampleAddress: 0x1000, Gen0Count: 60, Gen1Count: 30, Gen2Count: 10,
                Flags: TypeAggregateFlags.IsStringType, Gen2TotalSize: 12_345),
            [0x2000] = new TypeAggregateIndexEntry(
                MethodTable: 0x2000, ModuleId: 5, Count: 1, LohCount: 1, TotalSize: 200_000, LohSize: 200_000,
                SampleAddress: 0x2000, Gen0Count: 0, Gen1Count: 0, Gen2Count: 0, Gen2TotalSize: 0),
        };

        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.TypeAggregates);
            TypeAggregateIndexWriter.Write(writer.Stream, typeAggregates, modules: null, sizeBuckets: null,
                shapeCache: null, objectCount: 101);
            writer.EndSection(typeAggregates.Count);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        TypeAggregateIndexReader.TryLoad(reader!, containerPath, objectCount: 101, out var result).Should().BeTrue();

        result!.TypeAggregates[0x1000].Gen2TotalSize.Should().Be(12_345);
        result.TypeAggregates[0x1000].Gen2Count.Should().Be(10);
        result.TypeAggregates[0x2000].Gen2TotalSize.Should().Be(0);
    }
}
