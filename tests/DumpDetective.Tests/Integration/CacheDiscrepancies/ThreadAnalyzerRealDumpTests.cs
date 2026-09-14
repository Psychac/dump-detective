using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Threads;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="ThreadAnalyzer"/> produces an internally consistent result against
/// a real, large dump's real thread/stack state, run standalone (outside the pipeline's shared
/// thread-stack scan, so this exercises the live <c>RuntimeThreadQuery</c> fallback path, not the
/// precomputed one — the precomputed path is exercised indirectly by every full-pipeline real-dump
/// test that includes this analyzer).
/// </summary>
public sealed class ThreadAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ThreadAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using ThreadAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (ThreadDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Thread Analysis");
        result.TotalThreadCount.Should().BeGreaterThan(0, "a real IIS crash dump has managed threads");
        (result.AliveThreadCount + result.InactiveThreadCount).Should().Be(result.TotalThreadCount);
        result.BlockedThreadRatio.Should().BeInRange(0.0, 1.0);

        for (int i = 1; i < (result.TopLockedThreads?.Count ?? 0); i++)
            result.TopLockedThreads![i].LockCount.Should().BeLessThanOrEqualTo(result.TopLockedThreads[i - 1].LockCount);

        foreach (var t in result.TopLockedThreads ?? [])
            t.LockCount.Should().BeGreaterThan(0);

        foreach (var t in result.ThreadsWithActiveExceptions ?? [])
            t.ExceptionType.Should().NotBeNullOrEmpty();

        result.FinalizerThreadCount.Should().BeLessThanOrEqualTo(1, "a process has at most one finalizer thread");

        if (result.StackMemorySummary is not null)
        {
            result.StackMemorySummary.SampleCount.Should().BeGreaterThan(0);
            result.StackMemorySummary.TotalBytes.Should().BeGreaterThan(0UL);
            ((double)result.StackMemorySummary.MaxBytes).Should().BeGreaterThanOrEqualTo(result.StackMemorySummary.MeanBytes);
        }

        _output.WriteLine(
            $"TotalThreadCount={result.TotalThreadCount} AliveThreadCount={result.AliveThreadCount} " +
            $"GcThreadCount={result.GcThreadCount} BlockedThreadCount={result.BlockedThreadCount} " +
            $"LockHoldingThreadCount={result.LockHoldingThreadCount} ThreadsWithActiveExceptionsCount={result.ThreadsWithActiveExceptionsCount} " +
            $"BackgroundThreadCount={result.BackgroundThreadCount} ThreadPoolWorkerCount={result.ThreadPoolWorkerCount} " +
            $"FinalizerThreadCount={result.FinalizerThreadCount} AsyncChainThreadCount={result.AsyncChainThreadCount} " +
            $"MaxAsyncChainDepth={result.MaxAsyncChainDepth} StackMemorySummary={(result.StackMemorySummary is null ? "none" : $"{result.StackMemorySummary.SampleCount} samples")}");
    }
}
