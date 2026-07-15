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

public sealed class HangAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task HangAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        HangAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        HangDomainResult memResult = (HangDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.HangAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            HangDomainResult diskResult = (HangDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalAliveThreads.Should().Be(memResult.TotalAliveThreads);
            diskResult.WaitingThreadCount.Should().Be(memResult.WaitingThreadCount);
            diskResult.ThreadsHoldingLocks.Should().Be(memResult.ThreadsHoldingLocks);
            diskResult.WaitingPercent.Should().Be(memResult.WaitingPercent);
            diskResult.TotalTaskContinuations.Should().Be(memResult.TotalTaskContinuations);
            diskResult.QueuedWorkItems.Should().Be(memResult.QueuedWorkItems);
            diskResult.TotalTasks.Should().Be(memResult.TotalTasks);
            diskResult.PendingTasks.Should().Be(memResult.PendingTasks);
            diskResult.FaultedTasks.Should().Be(memResult.FaultedTasks);
            diskResult.CanceledTasks.Should().Be(memResult.CanceledTasks);
            diskResult.RuntimeThreadPoolDataAvailable.Should().Be(memResult.RuntimeThreadPoolDataAvailable);
            diskResult.RuntimeMinThreads.Should().Be(memResult.RuntimeMinThreads);
            diskResult.RuntimeMaxThreads.Should().Be(memResult.RuntimeMaxThreads);
            diskResult.RuntimeActiveWorkerThreads.Should().Be(memResult.RuntimeActiveWorkerThreads);
            diskResult.RuntimeIdleWorkerThreads.Should().Be(memResult.RuntimeIdleWorkerThreads);
            diskResult.RuntimeRetiredWorkerThreads.Should().Be(memResult.RuntimeRetiredWorkerThreads);
            diskResult.RuntimeQueueLength.Should().Be(memResult.RuntimeQueueLength);
            diskResult.RuntimeCpuUtilization.Should().Be(memResult.RuntimeCpuUtilization);
            diskResult.IsStarved.Should().Be(memResult.IsStarved);
            diskResult.TaskScanLimited.Should().Be(memResult.TaskScanLimited);
            diskResult.HealthScore.Should().Be(memResult.HealthScore);
            diskResult.WaitCategoryBreakdown.Count.Should().Be(memResult.WaitCategoryBreakdown.Count);
            (diskResult.TopWaitingThreads?.Count ?? 0).Should().Be(memResult.TopWaitingThreads?.Count ?? 0);
            (diskResult.TopContinuationTypes?.Count ?? 0).Should().Be(memResult.TopContinuationTypes?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
