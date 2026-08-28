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
        IReadOnlyList<WastefulCollectionSnapshot>? topWastefulCollections = null,
        Dictionary<string, int>? countsByElementType = null,
        Dictionary<string, ulong>? bytesByElementType = null) =>
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
            WasteBytesByKind: bytesByKind,
            WasteCountsByElementType: countsByElementType,
            WasteBytesByElementType: bytesByElementType);

    private static CompactTable? WasteByElementTypeTable(AnalyzerDetailSection section) =>
        section.CompactTables!.SingleOrDefault(t => t.Title == "Wasted memory by element type");

    [Fact]
    public void Build_ElementTypeTable_IncludesWastedBytesAndShare_OrderedDescending()
    {
        var result = BuildResult(
            totalWasted: 10_000,
            countsByKind: null,
            bytesByKind: null,
            countsByElementType: new() { ["System.String"] = 2, ["System.Int32"] = 4 },
            bytesByElementType: new() { ["System.String"] = 8_000, ["System.Int32"] = 2_000 });

        CompactTable table = WasteByElementTypeTable(new CollectionSectionBuilder().Build(result))!;

        table.Headers.Select(h => h.Name).Should().Equal("Element Type", "Wasteful Count", "Wasted", "Share of Waste");
        table.Rows.Select(r => r.Values[0]).Should().Equal("System.String", "System.Int32");
        table.Rows[0].Values[1].Should().Be(2L);
        table.Rows[0].Values[2].Should().Be(8_000L);
        table.Rows[0].Values[3].Should().Be(80.0);
    }

    [Fact]
    public void Build_NoElementTypeData_OmitsElementTypeTable()
    {
        var result = BuildResult(totalWasted: 0, countsByKind: null, bytesByKind: null);

        WasteByElementTypeTable(new CollectionSectionBuilder().Build(result)).Should().BeNull();
    }

    private static CompactTable WasteByKindTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Wasteful collections by kind");

    private static CompactTable WastefulCollectionsTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Wasteful collections");

    private static CompactTable? QueueSubTable(AnalyzerDetailSection section) =>
        section.CompactTables!.SingleOrDefault(t => t.Title == "Wasteful queues — buffer layout");

    [Fact]
    public void Build_WastefulCollectionsTable_DoesNotIncludeQueueOnlyColumns()
    {
        // Head/Tail/free-segment layout only means anything for Queue<T>'s circular buffer —
        // every other kind always rendered "—" here, so these columns moved to their own table.
        var snapshot = new WastefulCollectionSnapshot(
            Type: "System.Collections.Generic.Dictionary`2",
            Kind: CollectionKind.Dictionary,
            Count: 800,
            Capacity: 1000,
            FillRate: 80.0,
            WastedMemory: 8_000,
            Address: 0x1000);

        var result = BuildResult(totalWasted: 8_000, countsByKind: null, bytesByKind: null,
            topWastefulCollections: [snapshot]);

        CompactTable table = WastefulCollectionsTable(new CollectionSectionBuilder().Build(result));

        table.Headers.Select(h => h.Name).Should().NotContain(["Head", "Tail", "Largest Free Gap", "Free Segments"]);
    }

    [Fact]
    public void Build_QueueWastefulCollections_RendersBufferLayoutSubTable()
    {
        var queueSnapshot = new WastefulCollectionSnapshot(
            Type: "System.Collections.Generic.Queue`1",
            Kind: CollectionKind.Queue,
            Count: 100,
            Capacity: 1000,
            FillRate: 10.0,
            WastedMemory: 9_000,
            Address: 0x2000,
            Head: 50,
            Tail: 150,
            LargestContiguousFreeSegmentBytes: 800,
            FreeSegmentCount: 2);
        var dictionarySnapshot = new WastefulCollectionSnapshot(
            Type: "System.Collections.Generic.Dictionary`2",
            Kind: CollectionKind.Dictionary,
            Count: 800,
            Capacity: 1000,
            FillRate: 80.0,
            WastedMemory: 1_000,
            Address: 0x1000);

        var result = BuildResult(totalWasted: 10_000, countsByKind: null, bytesByKind: null,
            topWastefulCollections: [queueSnapshot, dictionarySnapshot]);

        CompactTable? queueTable = QueueSubTable(new CollectionSectionBuilder().Build(result));

        queueTable.Should().NotBeNull();
        queueTable!.Headers.Select(h => h.Name).Should().Equal("Type", "Count", "Capacity", "Head", "Tail", "Largest Free Gap", "Free Segments");
        queueTable.Rows.Should().ContainSingle();
        object?[] row = queueTable.Rows[0].Values;
        row[0].Should().Be("System.Collections.Generic.Queue`1");
        row[1].Should().Be(100L);
        row[2].Should().Be(1000L);
        row[3].Should().Be(50L);
        row[4].Should().Be(150L);
        row[5].Should().Be(800L);
        row[6].Should().Be(2L);
    }

    [Fact]
    public void Build_NoQueueWastefulCollections_OmitsBufferLayoutSubTable()
    {
        var snapshot = new WastefulCollectionSnapshot(
            Type: "System.Collections.Generic.List`1",
            Kind: CollectionKind.List,
            Count: 800,
            Capacity: 1000,
            FillRate: 80.0,
            WastedMemory: 8_000,
            Address: 0x1000);

        var result = BuildResult(totalWasted: 8_000, countsByKind: null, bytesByKind: null,
            topWastefulCollections: [snapshot]);

        QueueSubTable(new CollectionSectionBuilder().Build(result)).Should().BeNull();
    }

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
