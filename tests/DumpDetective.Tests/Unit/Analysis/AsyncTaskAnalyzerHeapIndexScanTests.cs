using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AsyncTaskAnalyzerHeapIndexScanTests
{
    private const ulong TaskMt = 0x2000;
    private const ulong OtherMt = 0x3000;

    [Fact]
    public void OnHeapEntry_OnlyAccumulatesEntries_WhoseMethodTableIsFlaggedAsTaskType()
    {
        AsyncTaskAnalyzer analyzer = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 100);

        analyzer.BeforeHeapIndexScan(context);
        analyzer.OnHeapEntry(new HeapEntry(0x1000, TaskMt, 100));
        analyzer.OnHeapEntry(new HeapEntry(0x1100, OtherMt, 100));
        analyzer.OnHeapEntry(new HeapEntry(0x1200, TaskMt, 100));

        var entries = GetParticipantEntries(analyzer);
        entries.Should().HaveCount(2);
        entries.Select(e => e.Address).Should().Equal(0x1000UL, 0x1200UL);
    }

    [Fact]
    public void OnHeapEntry_RespectsMaxTasksToScan()
    {
        AsyncTaskAnalyzer analyzer = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 2);

        analyzer.BeforeHeapIndexScan(context);
        analyzer.OnHeapEntry(new HeapEntry(0x1000, TaskMt, 100));
        analyzer.OnHeapEntry(new HeapEntry(0x1100, TaskMt, 100));
        analyzer.OnHeapEntry(new HeapEntry(0x1200, TaskMt, 100));

        var entries = GetParticipantEntries(analyzer);
        entries.Should().HaveCount(2);
    }

    [Fact]
    public void OnHeapEntry_AccumulatesTcsEntries_WhenMethodTableIsInTcsMtSet()
    {
        // BeforeHeapIndexScan's TCS-name resolution needs a live ClrHeap (not mockable in this
        // test setup), so _tcsMts is injected directly via reflection after BeforeHeapIndexScan
        // resets it — this isolates the OnHeapEntry/cap-enforcement mechanics under test from
        // the ClrMD-dependent name resolution, which has no unit-testable seam of its own.
        const ulong TcsMt = 0x4000;
        AsyncTaskAnalyzer analyzer = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 100);

        analyzer.BeforeHeapIndexScan(context);
        SetTcsMts(analyzer, [TcsMt]);

        analyzer.OnHeapEntry(new HeapEntry(0x1000, TaskMt, 100));
        analyzer.OnHeapEntry(new HeapEntry(0x2000, TcsMt, 50));
        analyzer.OnHeapEntry(new HeapEntry(0x2100, OtherMt, 50));
        analyzer.OnHeapEntry(new HeapEntry(0x2200, TcsMt, 50));

        var taskEntries = GetParticipantEntries(analyzer);
        taskEntries.Should().HaveCount(1);
        taskEntries.Select(e => e.Address).Should().Equal(0x1000UL);

        var tcsEntries = GetParticipantTcsEntries(analyzer);
        tcsEntries.Should().HaveCount(2);
        tcsEntries.Select(e => e.Address).Should().Equal(0x2000UL, 0x2200UL);
    }

    [Fact]
    public void OnHeapEntry_RespectsMaxTcsToScan()
    {
        const ulong TcsMt = 0x4000;
        AsyncTaskAnalyzer analyzer = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 100, maxTcsToScan: 1);

        analyzer.BeforeHeapIndexScan(context);
        SetTcsMts(analyzer, [TcsMt]);

        analyzer.OnHeapEntry(new HeapEntry(0x2000, TcsMt, 50));
        analyzer.OnHeapEntry(new HeapEntry(0x2100, TcsMt, 50));

        var tcsEntries = GetParticipantTcsEntries(analyzer);
        tcsEntries.Should().HaveCount(1);
    }

    [Fact]
    public void MergePartial_MergesTcsWorkerEntries_SortsByAddress_AndTrimsToGlobalCap()
    {
        const ulong TcsMt = 0x4000;
        AsyncTaskAnalyzer primary = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 100, maxTcsToScan: 3);

        primary.BeforeHeapIndexScan(context);
        SetTcsMts(primary, [TcsMt]);
        primary.OnHeapEntry(new HeapEntry(0x3000, TcsMt, 50));
        primary.OnHeapEntry(new HeapEntry(0x1000, TcsMt, 50));

        AsyncTaskAnalyzer worker = new();
        worker.BeforeHeapIndexScan(context);
        SetTcsMts(worker, [TcsMt]);
        worker.OnHeapEntry(new HeapEntry(0x2000, TcsMt, 50));
        worker.OnHeapEntry(new HeapEntry(0x4000, TcsMt, 50));

        primary.MergePartial([worker]);

        var merged = GetParticipantTcsEntries(primary);
        merged.Select(e => e.Address).Should().Equal(0x1000UL, 0x2000UL, 0x3000UL);
    }

    [Fact]
    public void MergePartial_MergesWorkerEntries_SortsByAddress_AndTrimsToGlobalCap()
    {
        // maxTasksToScan caps each worker individually (uncapped relative to the others), so
        // simulate two workers whose own local order is not address-sorted relative to each
        // other — the merge's re-sort, not either worker's own order, is what must make the
        // final trim to the global cap correct.
        AsyncTaskAnalyzer primary = new();
        AnalysisContext context = CreateContext(maxTasksToScan: 3);

        primary.BeforeHeapIndexScan(context);
        primary.OnHeapEntry(new HeapEntry(0x3000, TaskMt, 100));
        primary.OnHeapEntry(new HeapEntry(0x1000, TaskMt, 100));

        AsyncTaskAnalyzer worker = new();
        worker.BeforeHeapIndexScan(context);
        worker.OnHeapEntry(new HeapEntry(0x2000, TaskMt, 100));
        worker.OnHeapEntry(new HeapEntry(0x4000, TaskMt, 100));

        primary.MergePartial([worker]);

        var merged = GetParticipantEntries(primary);
        merged.Select(e => e.Address).Should().Equal(0x1000UL, 0x2000UL, 0x3000UL);
    }

    private static AnalysisContext CreateContext(int maxTasksToScan, int maxTcsToScan = 100)
    {
        HeapAnalysisCache cache = new();

        HeapIndexBuildResult buildResult = new(
            StorageKind: HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 0,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [TaskMt] = new TypeAggregateIndexEntry(
                    MethodTable: TaskMt,
                    ModuleId: 0,
                    Count: 1,
                    TotalSize: 100,
                    LohCount: 0,
                    LohSize: 0,
                    SampleAddress: 0x1000,
                    Flags: TypeAggregateFlags.IsTaskType)
            });

        object heapIndexCache = typeof(HeapAnalysisCache)
            .GetField("_heapIndexCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cache)!;

        heapIndexCache.GetType()
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(heapIndexCache, buildResult);

        return new RuntimeAnalysisContext
        {
            Runtime = null!,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions
            {
                AsyncTaskAnalysis = new AsyncTaskAnalysisOptions
                {
                    MaxTasksToScan = maxTasksToScan,
                    MaxTcsToScan = maxTcsToScan
                }
            }
        };
    }

    private static List<(ulong Address, ulong Mt, int StateFlags)> GetParticipantEntries(AsyncTaskAnalyzer analyzer)
    {
        return (List<(ulong Address, ulong Mt, int StateFlags)>)typeof(AsyncTaskAnalyzer)
            .GetField("_participantEntries", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
    }

    private static List<(ulong Address, ulong Mt)> GetParticipantTcsEntries(AsyncTaskAnalyzer analyzer)
    {
        return (List<(ulong Address, ulong Mt)>)typeof(AsyncTaskAnalyzer)
            .GetField("_participantTcsEntries", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
    }

    // BeforeHeapIndexScan's TCS candidate-set construction resolves type names via a live
    // ClrHeap, which isn't mockable in this test setup (Runtime = null!) — inject the resulting
    // set directly to isolate OnHeapEntry/MergePartial's TCS-path mechanics under test.
    private static void SetTcsMts(AsyncTaskAnalyzer analyzer, HashSet<ulong> tcsMts)
    {
        typeof(AsyncTaskAnalyzer)
            .GetField("_tcsMts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, tcsMts);
    }
}
