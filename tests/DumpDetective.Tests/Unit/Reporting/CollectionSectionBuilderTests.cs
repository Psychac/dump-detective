using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class CollectionSectionBuilderTests
{
    private static CollectionDomainResult BuildResult(
        ulong totalWasted,
        Dictionary<CollectionKind, int>? countsByKind,
        Dictionary<CollectionKind, ulong>? bytesByKind) =>
        new(
            TotalCollections: 100,
            Dictionaries: 20,
            Lists: 20,
            ArrayLists: 0,
            Stacks: 0,
            SortedLists: 0,
            SortedSets: 0,
            HashSets: 0,
            Queues: 10,
            TotalWastedMemory: totalWasted,
            WastefulCollectionCount: 6,
            WasteCountsByKind: countsByKind,
            WasteBytesByKind: bytesByKind);

    private static CompactTable WasteByKindTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Wasteful collections by kind");

    [Fact]
    public void Build_PerKindTable_IncludesWastedBytesAndShare()
    {
        var result = BuildResult(
            totalWasted: 10_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 2, [CollectionKind.Queue] = 4 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 8_000, [CollectionKind.Queue] = 2_000 });

        CompactTable table = WasteByKindTable(new CollectionSectionBuilder().Build(result));

        table.Headers.Select(h => h.Name).Should().Equal("Kind", "Wasteful Count", "Wasted", "Share of Waste");
        table.Rows[0].Values[0].Should().Be("Dictionary");
        table.Rows[0].Values[2].Should().Be(8_000L);
        table.Rows[0].Values[3].Should().Be(80.0);
    }

    [Fact]
    public void Build_PerKindTable_OrdersKindsByWastedBytesDescending()
    {
        var result = BuildResult(
            totalWasted: 10_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 9, [CollectionKind.Queue] = 1 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 1_000, [CollectionKind.Queue] = 9_000 });

        CompactTable table = WasteByKindTable(new CollectionSectionBuilder().Build(result));

        table.Rows.Select(r => r.Values[0]).Should().Equal("Queue", "Dictionary");
    }

    [Fact]
    public void Build_KindWithoutByteData_RendersZeroWastedInsteadOfFailing()
    {
        var result = BuildResult(
            totalWasted: 0,
            countsByKind: new() { [CollectionKind.Dictionary] = 2 },
            bytesByKind: null);

        CompactTable table = WasteByKindTable(new CollectionSectionBuilder().Build(result));

        table.Rows.Should().ContainSingle();
        table.Rows[0].Values[2].Should().Be(0L);
        table.Rows[0].Values[3].Should().Be(0.0);
    }
}
