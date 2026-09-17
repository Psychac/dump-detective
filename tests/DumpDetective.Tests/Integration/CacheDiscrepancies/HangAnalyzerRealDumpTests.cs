using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Threads;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="HangAnalyzer"/> produces an internally consistent result against
/// a real, large dump's real thread state, thread pool, and heap-index scan. The heap-index-scan
/// side is driven explicitly via <see cref="HeapIndexScanDispatcher"/> (matching
/// <c>TimerLeakAnalyzerRealDumpTests</c>' own pattern) — without it, the adapter's own
/// "invoked outside the pipeline" default (<c>_heapScanSucceeded = true</c>, empty candidate list)
/// would silently report zero tasks/continuations regardless of what the dump actually contains, the
/// same accepted-but-easy-to-miss behavior every candidate-streaming Tier-2 adapter shares. The
/// thread-stack side is left on its live fallback (matching <c>LockGraphAnalyzerRealDumpTests</c>'
/// own precedent), since that path already exercises real thread data without needing the dispatcher.
/// </summary>
public sealed class HangAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public HangAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using HangAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (HangDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Hang Analysis");
        result.TotalAliveThreads.Should().BeGreaterThan(0, "a real IIS crash dump has alive managed threads");
        result.WaitingPercent.Should().BeInRange(0.0, 100.0);
        HangHealthClassifier.ComputeHealthScore(result).Should().BeInRange(0, 100);

        for (int i = 1; i < (result.TopWaitingThreads?.Count ?? 0); i++)
            result.TopWaitingThreads![i].LockCount.Should().BeLessThanOrEqualTo(result.TopWaitingThreads[i - 1].LockCount);

        result.TotalTasks.Should().BeGreaterThanOrEqualTo(result.PendingTasks + result.FaultedTasks + result.CanceledTasks,
            "pending/faulted/canceled are a subset of scanned tasks, not additional to it");

        if (result.RuntimeThreadPoolDataAvailable)
        {
            result.RuntimeMaxThreads.Should().BeGreaterThanOrEqualTo(0);
            result.RuntimeActiveWorkerThreads.Should().BeGreaterThanOrEqualTo(0);
        }

        _output.WriteLine(
            $"TotalAliveThreads={result.TotalAliveThreads} WaitingThreadCount={result.WaitingThreadCount} " +
            $"ThreadsHoldingLocks={result.ThreadsHoldingLocks} HealthScore={HangHealthClassifier.ComputeHealthScore(result)} " +
            $"TotalTasks={result.TotalTasks} PendingTasks={result.PendingTasks} FaultedTasks={result.FaultedTasks} " +
            $"CanceledTasks={result.CanceledTasks} QueuedWorkItems={result.QueuedWorkItems} " +
            $"TotalTaskContinuations={result.TotalTaskContinuations} RuntimeThreadPoolDataAvailable={result.RuntimeThreadPoolDataAvailable} " +
            $"IsStarved={HangHealthClassifier.IsStarved(result)}");
    }
}
