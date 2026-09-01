using DumpDetective.Analysis.Indexing;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Covers the I-9 (docs/analysis/phase1/reference-chain-analyzer-audit.md) sample-address
/// tie-break: <see cref="TypeIndexBuilder"/> should prefer a longer-lived sample (LOH/Gen2 over
/// Gen1 over Gen0/unknown) over the previously-unconditional "lowest address wins" rule, both
/// within a single builder (<see cref="TypeIndexBuilder.Add"/>) and across parallel partial
/// builders (<see cref="TypeIndexBuilder.Merge"/>).
/// </summary>
public class TypeIndexBuilderTests
{
    private const ulong MethodTable = 0x1000;

    [Fact]
    public void Add_Gen2AfterGen0_PrefersGen2SampleEvenAtHigherAddress()
    {
        var builder = new TypeIndexBuilder();
        builder.Add(new HeapEntry(address: 100, MethodTable, size: 24), generation: 0);
        builder.Add(new HeapEntry(address: 200, MethodTable, size: 24), generation: 2);

        var result = builder.Build();

        result[MethodTable].SampleAddress.Should().Be(200);
    }

    [Fact]
    public void Add_Gen0AfterGen2_KeepsGen2SampleDespiteLowerAddress()
    {
        var builder = new TypeIndexBuilder();
        builder.Add(new HeapEntry(address: 200, MethodTable, size: 24), generation: 2);
        builder.Add(new HeapEntry(address: 100, MethodTable, size: 24), generation: 0);

        var result = builder.Build();

        result[MethodTable].SampleAddress.Should().Be(200);
    }

    [Fact]
    public void Add_LohObject_PreferredOverGen0RegardlessOfGenerationValue()
    {
        var builder = new TypeIndexBuilder();
        builder.Add(new HeapEntry(address: 200, MethodTable, size: 24), generation: 0);
        // LOH objects are identified by size, not by the generation parameter — pass -1 (unknown)
        // to prove the size threshold alone drives the tier.
        builder.Add(new HeapEntry(address: 100, MethodTable, size: 100_000), generation: -1);

        var result = builder.Build();

        result[MethodTable].SampleAddress.Should().Be(100);
    }

    [Fact]
    public void Add_SameTierTwice_LowestAddressWinsWithinTier()
    {
        var builder = new TypeIndexBuilder();
        builder.Add(new HeapEntry(address: 300, MethodTable, size: 24), generation: 2);
        builder.Add(new HeapEntry(address: 150, MethodTable, size: 24), generation: 2);

        var result = builder.Build();

        result[MethodTable].SampleAddress.Should().Be(150);
    }

    [Fact]
    public void Add_UnknownGenerationTwice_LowestAddressWins()
    {
        var builder = new TypeIndexBuilder();
        builder.Add(new HeapEntry(address: 300, MethodTable, size: 24), generation: -1);
        builder.Add(new HeapEntry(address: 150, MethodTable, size: 24), generation: -1);

        var result = builder.Build();

        result[MethodTable].SampleAddress.Should().Be(150);
    }

    [Fact]
    public void Merge_OtherHasHigherTier_TakesOtherSampleRegardlessOfAddress()
    {
        var main = new TypeIndexBuilder();
        main.Add(new HeapEntry(address: 100, MethodTable, size: 24), generation: 0);

        var other = new TypeIndexBuilder();
        other.Add(new HeapEntry(address: 500, MethodTable, size: 24), generation: 2);

        main.Merge(other);

        var result = main.Build();
        result[MethodTable].SampleAddress.Should().Be(500);
    }

    [Fact]
    public void Merge_SameTier_LowestAddressWins()
    {
        var main = new TypeIndexBuilder();
        main.Add(new HeapEntry(address: 500, MethodTable, size: 24), generation: 2);

        var other = new TypeIndexBuilder();
        other.Add(new HeapEntry(address: 300, MethodTable, size: 24), generation: 2);

        main.Merge(other);

        var result = main.Build();
        result[MethodTable].SampleAddress.Should().Be(300);
    }
}
