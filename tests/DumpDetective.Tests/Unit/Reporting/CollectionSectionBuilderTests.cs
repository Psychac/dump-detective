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
        Dictionary<CollectionKind, ulong>? bytesByKind,
        IReadOnlyList<WastefulCollectionSnapshot>? topWastefulCollections = null) =>
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
            TopWastefulCollections: topWastefulCollections,
            WasteCountsByKind: countsByKind,
            WasteBytesByKind: bytesByKind);

    private static CompactTable WasteByKindTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Wasteful collections by kind");

    private static CompactTable WastefulCollectionsTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Wasteful collections");

    [Fact]
    public void Build_WastefulCollectionsTable_IncludesRecommendationColumn()
    {
        var snapshot = new WastefulCollectionSnapshot(
            Type: "System.Collections.Generic.Dictionary`2",
            Kind: CollectionKind.Dictionary,
            Count: 800,
            Capacity: 1000,
            FillRate: 80.0,
            WastedMemory: 8_000,
            Address: 0x1000,
            Recommendation: "Call TrimExcess() once population is complete to release unused capacity.");

        var result = BuildResult(totalWasted: 8_000, countsByKind: null, bytesByKind: null,
            topWastefulCollections: [snapshot]);

        CompactTable table = WastefulCollectionsTable(new CollectionSectionBuilder().Build(result));

        table.Headers.Select(h => h.Name).Should().Contain("Recommendation");
        table.Rows[0].Values[^1].Should().Be(snapshot.Recommendation);
    }

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
