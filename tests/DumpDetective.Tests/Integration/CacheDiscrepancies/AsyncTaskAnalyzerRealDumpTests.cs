using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Async;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="AsyncTaskAnalyzer"/> produces an internally consistent result
/// against a real, large dump -- covering the unified Task/TaskCompletionSource/IValueTaskSource
/// candidate stream, continuation-chain BFS, and the new <c>TryReadNestedStructField</c>/
/// <c>EnumerateObjectReferenceElements</c> Tier-2 primitives it introduced.
/// </summary>
public sealed class AsyncTaskAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public AsyncTaskAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using AsyncTaskAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AsyncTaskDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Async Task Analysis");

        // ── Task state accounting ────────────────────────────────────────────
        (result.PendingTasks + result.RunningTasks + result.FaultedTasks + result.CanceledTasks + result.CompletedTasks)
            .Should().Be(result.TotalTasks);
        result.OrphanedTasks.Should().BeLessThanOrEqualTo(result.TotalTasks);
        (result.PendingGen0 + result.PendingGen1 + result.PendingGen2 + result.PendingLOH)
            .Should().BeLessThanOrEqualTo(result.PendingTasks);

        // ── Continuation-chain BFS ────────────────────────────────────────────
        result.DepthSampleCount.Should().BeLessThanOrEqualTo(result.TotalTasks);
        result.MaxContinuationDepth.Should().BeGreaterThanOrEqualTo(0);
        result.MultiContinuationNodeCount.Should().BeGreaterThanOrEqualTo(0);
        result.MaxContinuationFanOut.Should().BeGreaterThanOrEqualTo(0);
        for (int i = 1; i < result.TopDeepestChains.Count; i++)
            result.TopDeepestChains[i].Depth.Should().BeLessThanOrEqualTo(result.TopDeepestChains[i - 1].Depth);
        for (int i = 1; i < result.TopPendingTaskTypes.Count; i++)
            result.TopPendingTaskTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopPendingTaskTypes[i - 1].Count);
        for (int i = 1; i < result.TopFaultedTaskTypes.Count; i++)
            result.TopFaultedTaskTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopFaultedTaskTypes[i - 1].Count);

        // ── TaskCompletionSource / IValueTaskSource population health ────────
        result.UnresolvedTaskCompletionSources.Should().BeLessThanOrEqualTo(result.TotalTaskCompletionSources);
        result.UnresolvedTcsGen2Count.Should().BeLessThanOrEqualTo(result.UnresolvedTaskCompletionSources);
        (result.TopUnresolvedTaskCompletionSources?.Count ?? 0).Should().Be(result.UnresolvedTaskCompletionSources);

        result.PendingValueTaskSources.Should().BeLessThanOrEqualTo(result.TotalValueTaskSources);
        result.PendingVtsGen2Count.Should().BeLessThanOrEqualTo(result.PendingValueTaskSources);
        (result.TopPendingValueTaskSources?.Count ?? 0).Should().Be(result.PendingValueTaskSources);

        foreach (UnresolvedTcsSnapshot snapshot in result.TopUnresolvedTaskCompletionSources ?? [])
            snapshot.Generation.Should().BeInRange(-1, 3);
        foreach (PendingValueTaskSourceSnapshot snapshot in result.TopPendingValueTaskSources ?? [])
            snapshot.Generation.Should().BeInRange(-1, 3);

        _output.WriteLine(
            $"TotalTasks={result.TotalTasks} Pending={result.PendingTasks} Running={result.RunningTasks} " +
            $"Faulted={result.FaultedTasks} Canceled={result.CanceledTasks} Completed={result.CompletedTasks} " +
            $"Orphaned={result.OrphanedTasks} MaxContinuationDepth={result.MaxContinuationDepth} " +
            $"MultiContinuationNodeCount={result.MultiContinuationNodeCount} MaxContinuationFanOut={result.MaxContinuationFanOut} " +
            $"TotalTaskCompletionSources={result.TotalTaskCompletionSources} UnresolvedTaskCompletionSources={result.UnresolvedTaskCompletionSources} " +
            $"TotalValueTaskSources={result.TotalValueTaskSources} PendingValueTaskSources={result.PendingValueTaskSources}");
    }
}
