using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshCollectionAnalyzer()
    {
        CollectionAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<CollectionAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsCollectionKindCounters()
    {
        CollectionAnalyzer primary = new();
        SeedParticipantState(primary, new CollectionStatistics
        {
            TotalCollections = 10,
            Dictionaries = 4,
            Lists = 3,
            HashSets = 2,
            Queues = 1
        });

        CollectionAnalyzer worker = new();
        SeedParticipantState(worker, new CollectionStatistics
        {
            TotalCollections = 8,
            Dictionaries = 2,
            Lists = 5,
            ArrayLists = 1
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        CollectionStatistics merged = GetStats(primary);
        merged.TotalCollections.Should().Be(18);
        merged.Dictionaries.Should().Be(6);
        merged.Lists.Should().Be(8);
        merged.HashSets.Should().Be(2);
        merged.Queues.Should().Be(1);
        merged.ArrayLists.Should().Be(1);
    }

    [Fact]
    public void MergePartial_SumsWastefulCountAndTotalWasted()
    {
        CollectionAnalyzer primary = new();
        SeedParticipantState(primary, new CollectionStatistics(), wastefulCount: 3, totalWasted: 1024);

        CollectionAnalyzer worker = new();
        SeedParticipantState(worker, new CollectionStatistics(), wastefulCount: 2, totalWasted: 512);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetWastefulCount(primary).Should().Be(5);
        GetTotalWasted(primary).Should().Be(1536ul);
    }

    [Fact]
    public void MergePartial_CombinesWastefulLists_TrimsToTopCapacity()
    {
        // topCapacity = 3; primary and worker each have 2 entries; only the top-3 by WastedMemory should survive.
        CollectionAnalyzer primary = new();
        SeedParticipantState(primary, new CollectionStatistics(), topCapacity: 3, wasteful: new List<WastefulCollection>
        {
            new() { Address = 0x1000, WastedMemory = 200 },
            new() { Address = 0x2000, WastedMemory = 100 }
        });

        CollectionAnalyzer worker = new();
        SeedParticipantState(worker, new CollectionStatistics(), topCapacity: 3, wasteful: new List<WastefulCollection>
        {
            new() { Address = 0x3000, WastedMemory = 500 },
            new() { Address = 0x4000, WastedMemory =  50 }
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var wasteful = GetWasteful(primary);
        wasteful.Should().HaveCount(3);
        wasteful.Select(w => w.WastedMemory).Should().BeEquivalentTo(new ulong[] { 500, 200, 100 });
    }

    [Fact]
    public void MergePartial_UnionsMethodTableKindCache_ExistingKeyWins()
    {
        CollectionAnalyzer primary = new();
        SeedParticipantState(primary, new CollectionStatistics(),
            methodTableKinds: new() { [0x1000UL] = CollectionKind.Dictionary });

        CollectionAnalyzer worker = new();
        SeedParticipantState(worker, new CollectionStatistics(),
            methodTableKinds: new() { [0x1000UL] = CollectionKind.List, [0x2000UL] = CollectionKind.HashSet });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var cache = GetMethodTableKinds(primary);
        cache[0x1000UL].Should().Be(CollectionKind.Dictionary); // existing value preserved
        cache[0x2000UL].Should().Be(CollectionKind.HashSet);    // new key added
    }

    [Fact]
    public void MergePartial_SumsGenerationCounts_PerKindPerBucket()
    {
        CollectionAnalyzer primary = new();
        var genCounts = new Dictionary<CollectionKind, int[]>
        {
            [CollectionKind.Dictionary] = [1, 2, 3, 0],
            [CollectionKind.List] = [0, 1, 0, 0]
        };
        SeedParticipantState(primary, new CollectionStatistics(), generationCounts: genCounts);

        CollectionAnalyzer worker = new();
        var workerGenCounts = new Dictionary<CollectionKind, int[]>
        {
            [CollectionKind.Dictionary] = [0, 1, 2, 1],
            [CollectionKind.HashSet] = [5, 0, 0, 0]
        };
        SeedParticipantState(worker, new CollectionStatistics(), generationCounts: workerGenCounts);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var result = GetGenerationCounts(primary);
        result[CollectionKind.Dictionary].Should().Equal([1, 3, 5, 1]);
        result[CollectionKind.List].Should().Equal([0, 1, 0, 0]);
        result[CollectionKind.HashSet].Should().Equal([5, 0, 0, 0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static void SeedParticipantState(
        CollectionAnalyzer analyzer,
        CollectionStatistics stats,
        int topCapacity = 10,
        List<WastefulCollection>? wasteful = null,
        int wastefulCount = 0,
        ulong totalWasted = 0,
        Dictionary<ulong, CollectionKind>? methodTableKinds = null,
        Dictionary<CollectionKind, int[]>? generationCounts = null)
    {
        Type t = typeof(CollectionAnalyzer);
        SetField(t, analyzer, "_stats", stats);
        SetField(t, analyzer, "_topCapacity", topCapacity);
        SetField(t, analyzer, "_wasteful", wasteful ?? new List<WastefulCollection>());
        SetField(t, analyzer, "_wastefulCount", wastefulCount);
        SetField(t, analyzer, "_totalWasted", totalWasted);
        SetField(t, analyzer, "_methodTableKinds", methodTableKinds ?? new Dictionary<ulong, CollectionKind>());

        if (generationCounts != null)
        {
            SetField(t, analyzer, "_generationCounts", generationCounts);
        }
        else
        {
            var defaultGenCounts = new Dictionary<CollectionKind, int[]>();
            foreach (CollectionKind k in Enum.GetValues(typeof(CollectionKind)))
                defaultGenCounts[k] = new int[4];
            SetField(t, analyzer, "_generationCounts", defaultGenCounts);
        }
    }

    private static void SetField(Type type, object instance, string name, object? value) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static T GetField<T>(CollectionAnalyzer analyzer, string name) =>
        (T)typeof(CollectionAnalyzer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(analyzer)!;

    private static CollectionStatistics GetStats(CollectionAnalyzer a) => GetField<CollectionStatistics>(a, "_stats");
    private static int GetWastefulCount(CollectionAnalyzer a) => GetField<int>(a, "_wastefulCount");
    private static ulong GetTotalWasted(CollectionAnalyzer a) => GetField<ulong>(a, "_totalWasted");
    private static List<WastefulCollection> GetWasteful(CollectionAnalyzer a) => GetField<List<WastefulCollection>>(a, "_wasteful");
    private static Dictionary<ulong, CollectionKind> GetMethodTableKinds(CollectionAnalyzer a) => GetField<Dictionary<ulong, CollectionKind>>(a, "_methodTableKinds");
    private static Dictionary<CollectionKind, int[]> GetGenerationCounts(CollectionAnalyzer a) => GetField<Dictionary<CollectionKind, int[]>>(a, "_generationCounts");
}
