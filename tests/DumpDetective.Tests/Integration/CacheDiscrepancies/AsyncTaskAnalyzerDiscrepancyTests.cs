using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class AsyncTaskAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task AsyncTaskAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        AsyncTaskAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        AsyncTaskDomainResult memResult = (AsyncTaskDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.AsyncTaskAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            AsyncTaskDomainResult diskResult = (AsyncTaskDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalTasks.Should().Be(memResult.TotalTasks);
            diskResult.PendingTasks.Should().Be(memResult.PendingTasks);
            diskResult.RunningTasks.Should().Be(memResult.RunningTasks);
            diskResult.FaultedTasks.Should().Be(memResult.FaultedTasks);
            diskResult.CanceledTasks.Should().Be(memResult.CanceledTasks);
            diskResult.CompletedTasks.Should().Be(memResult.CompletedTasks);
            diskResult.OrphanedTasks.Should().Be(memResult.OrphanedTasks);
            diskResult.TotalTaskContinuations.Should().Be(memResult.TotalTaskContinuations);
            diskResult.MaxContinuationDepth.Should().Be(memResult.MaxContinuationDepth);
            diskResult.AvgContinuationDepth.Should().Be(memResult.AvgContinuationDepth);
            diskResult.TaskScanLimited.Should().Be(memResult.TaskScanLimited);
            diskResult.TopPendingTaskTypes.Count.Should().Be(memResult.TopPendingTaskTypes.Count);
            diskResult.TopFaultedTaskTypes.Count.Should().Be(memResult.TopFaultedTaskTypes.Count);
            diskResult.TopContinuationTypes.Count.Should().Be(memResult.TopContinuationTypes.Count);
            diskResult.TopOrphanedTasks.Count.Should().Be(memResult.TopOrphanedTasks.Count);
            diskResult.TopDeepestChains.Count.Should().Be(memResult.TopDeepestChains.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
