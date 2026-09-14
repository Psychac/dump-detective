using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="FinalizableObjectAnalyzer"/> produces an internally consistent
/// result against a real, large dump's real finalizer queue and type population -- the actual
/// behavioral verification the live-process characterization tests deliberately don't attempt,
/// since a real dump's finalizer queue is a genuinely populated, non-transient snapshot.
/// </summary>
public sealed class FinalizableObjectAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public FinalizableObjectAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using FinalizableObjectAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (FinalizableObjectDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Finalizable Object Analysis");
        result.TotalFinalizableObjects.Should().BeGreaterThan(0, "a real 3.5 GB IIS dump has finalizable objects (streams, handles, timers, ...)");
        result.FinalizerQueueCount.Should().BeGreaterThanOrEqualTo(0);

        for (int i = 1; i < result.TopFinalizableTypesByGen2Count.Count; i++)
            result.TopFinalizableTypesByGen2Count[i].Gen2Count.Should().BeLessThanOrEqualTo(result.TopFinalizableTypesByGen2Count[i - 1].Gen2Count);

        for (int i = 1; i < result.TopQueueEntriesByRetainedSize.Count; i++)
            result.TopQueueEntriesByRetainedSize[i].EstimatedRetainedBytes.Should().BeLessThanOrEqualTo(result.TopQueueEntriesByRetainedSize[i - 1].EstimatedRetainedBytes);

        int classifiedByType = 0;
        foreach (var t in result.TopQueueTypesByCount)
            classifiedByType += t.QueueCount;
        classifiedByType.Should().BeLessThanOrEqualTo(result.FinalizerQueueCount);

        result.CriticalFinalizerQueueCount.Should().BeLessThanOrEqualTo(result.FinalizerQueueCount);

        int sampledRootPaths = 0;
        foreach (var e in result.TopQueueEntriesByRetainedSize)
            if (e.SampleRootPath is not null) sampledRootPaths++;
        sampledRootPaths.Should().BeLessThanOrEqualTo(10, "root-path search is capped to the top 10 entries by retained size");

        _output.WriteLine(
            $"TotalFinalizableObjects={result.TotalFinalizableObjects:N0} FinalizerQueueCount={result.FinalizerQueueCount:N0} " +
            $"CriticalFinalizerQueueCount={result.CriticalFinalizerQueueCount:N0} HasUndisposedDisposableInQueue={result.HasUndisposedDisposableInQueue} " +
            $"IsRetainedEstimatePartial={result.IsRetainedEstimatePartial} TopQueueTypesByCount={result.TopQueueTypesByCount.Count} " +
            $"TopFinalizableTypesByGen2Count={result.TopFinalizableTypesByGen2Count.Count} RootPathsSampled={sampledRootPaths}");
    }
}
