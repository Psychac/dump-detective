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

    private static AnalysisContext CreateContext(int maxTasksToScan)
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
                    MaxTasksToScan = maxTasksToScan
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
}
