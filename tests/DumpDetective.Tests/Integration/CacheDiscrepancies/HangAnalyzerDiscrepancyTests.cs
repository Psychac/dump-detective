using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class HangAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task HangAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        HangDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache, analysisOptions);
        string freshDumpPath = dumpPath + ".freshdiskcheck.HangAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            HangDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache, analysisOptions);
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

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    // A fresh HangAnalyzer instance is used per call since the analyzer now carries instance
    // accumulator state primed by BeforeHeapIndexScan.
    private static async Task<HangDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache, AnalysisOptions analysisOptions)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };
        AnalysisPipeline pipeline = new([new HangAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<HangDomainResult>()!;
    }
}
