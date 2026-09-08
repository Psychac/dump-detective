using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionTrendComparerTests
{
    private static CollectionDomainResult MakeResult(
        ulong totalWasted,
        Dictionary<CollectionKind, int>? countsByKind = null,
        Dictionary<CollectionKind, ulong>? bytesByKind = null) =>
        new(
            TotalCollections: 100,
            Dictionaries: 10,
            Lists: 10,
            ArrayLists: 0,
            Stacks: 0,
            SortedLists: 0,
            SortedSets: 0,
            HashSets: 0,
            Queues: 0,
            TotalWastedMemory: totalWasted,
            WastefulCollectionCount: 5,
            WasteCountsByKind: countsByKind,
            WasteBytesByKind: bytesByKind);

    [Fact]
    public void ExtractMetrics_EmitsPerKindWastedBytes()
    {
        var result = MakeResult(3_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 2 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 3_000 });

        var metrics = new CollectionTrendComparer().ExtractMetrics(result);

        metrics.Should().Contain(m =>
            m.Key == "collection.waste.kind.bytes" && m.Scope == "Dictionary" && m.Value == 3_000);
    }

    [Fact]
    public void Compare_EmitsPerKindByteDeltas()
    {
        var baseline = MakeResult(1_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 1 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 1_000 });
        var current = MakeResult(4_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 3 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 4_000 });

        var deltas = new CollectionTrendComparer().Compare(baseline, current);

        deltas.Should().Contain(d =>
            d.Key == "collection.waste.kind.bytes" && d.Scope == "Dictionary" && d.Delta == 3_000);
        deltas.Should().Contain(d =>
            d.Key == "collection.waste.kind.count" && d.Scope == "Dictionary" && d.Delta == 2);
    }

    [Fact]
    public void Compare_KindPresentInOnlyOneSnapshot_StillEmitsDelta()
    {
        var baseline = MakeResult(1_000,
            countsByKind: new() { [CollectionKind.Dictionary] = 1 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 1_000 });
        var current = MakeResult(500,
            countsByKind: new() { [CollectionKind.Queue] = 1 },
            bytesByKind: new() { [CollectionKind.Queue] = 500 });

        var deltas = new CollectionTrendComparer().Compare(baseline, current);

        deltas.Should().Contain(d =>
            d.Key == "collection.waste.kind.bytes" && d.Scope == "Dictionary" && d.Current == 0 && d.Delta == -1_000);
        deltas.Should().Contain(d =>
            d.Key == "collection.waste.kind.bytes" && d.Scope == "Queue" && d.Baseline == 0 && d.Delta == 500);
    }

    [Fact]
    public void Compare_NoPerKindData_EmitsOnlyTotals()
    {
        var deltas = new CollectionTrendComparer().Compare(MakeResult(1_000), MakeResult(2_000));

        deltas.Should().NotContain(d => d.Key.StartsWith("collection.waste.kind."));
        deltas.Should().Contain(d => d.Key == "collection.wasted.bytes" && d.Delta == 1_000);
    }
}
