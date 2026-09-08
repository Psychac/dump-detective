using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Indexing;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// P2-2: <see cref="StringAnalyzer"/> must surface Gen0/Gen1 string counts alongside the
/// existing Gen2 count/bytes, sourced from the same <see cref="TypeAggregateIndexEntry"/>
/// per-generation fields.
/// </summary>
public sealed class StringAnalyzerGenerationStatsTests
{
    private static readonly MethodInfo AggregateMethod =
        typeof(StringAnalyzer).GetMethod("AggregateStringTypeStats", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void AggregateStringTypeStats_SumsPerGenerationCounts_AcrossStringTypes()
    {
        var typeAggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            [0x1000] = new TypeAggregateIndexEntry(
                MethodTable: 0x1000, ModuleId: 0, Count: 100, TotalSize: 5000, LohCount: 0, LohSize: 0,
                SampleAddress: 0, Gen0Count: 60, Gen1Count: 30, Gen2Count: 10),
            [0x2000] = new TypeAggregateIndexEntry(
                MethodTable: 0x2000, ModuleId: 0, Count: 50, TotalSize: 2500, LohCount: 0, LohSize: 0,
                SampleAddress: 0, Gen0Count: 20, Gen1Count: 20, Gen2Count: 10),
        };
        var stringMts = new HashSet<ulong> { 0x1000, 0x2000 };

        var result = Invoke(typeAggregates, stringMts);

        result.TotalStrings.Should().Be(150);
        result.Gen0StringCount.Should().Be(80);
        result.Gen1StringCount.Should().Be(50);
        result.Gen2StringCount.Should().Be(20);
    }

    [Fact]
    public void AggregateStringTypeStats_IgnoresMethodTables_NotInTypeAggregates()
    {
        var typeAggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            [0x1000] = new TypeAggregateIndexEntry(
                MethodTable: 0x1000, ModuleId: 0, Count: 10, TotalSize: 100, LohCount: 0, LohSize: 0,
                SampleAddress: 0, Gen0Count: 5, Gen1Count: 3, Gen2Count: 2),
        };
        var stringMts = new HashSet<ulong> { 0x1000, 0x9999 };

        var result = Invoke(typeAggregates, stringMts);

        result.Gen0StringCount.Should().Be(5);
        result.Gen1StringCount.Should().Be(3);
        result.Gen2StringCount.Should().Be(2);
    }

    private static (int TotalStrings, ulong TotalStringMemory, ulong LohStringBytes,
        long Gen0StringCount, long Gen1StringCount, long Gen2StringCount, ulong Gen2StringBytes) Invoke(
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> typeAggregates, IReadOnlySet<ulong> stringMts)
    {
        object raw = AggregateMethod.Invoke(null, [typeAggregates, stringMts])!;
        Type t = raw.GetType();
        return (
            (int)t.GetField("Item1")!.GetValue(raw)!,
            (ulong)t.GetField("Item2")!.GetValue(raw)!,
            (ulong)t.GetField("Item3")!.GetValue(raw)!,
            (long)t.GetField("Item4")!.GetValue(raw)!,
            (long)t.GetField("Item5")!.GetValue(raw)!,
            (long)t.GetField("Item6")!.GetValue(raw)!,
            (ulong)t.GetField("Item7")!.GetValue(raw)!);
    }
}
