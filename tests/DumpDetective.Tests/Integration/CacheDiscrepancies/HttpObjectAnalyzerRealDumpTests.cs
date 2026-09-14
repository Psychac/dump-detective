using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Infra;
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
/// proves <see cref="HttpObjectAnalyzer"/> produces an internally consistent result
/// against a real, large dump.
/// </summary>
public sealed class HttpObjectAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public HttpObjectAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using HttpObjectAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (HttpObjectDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("HTTP Object Analysis");

        if (!result.HttpObjectsFound)
        {
            result.TotalHttpObjects.Should().Be(0);
            _output.WriteLine("No HTTP-shaped objects found on this dump's heap -- empty result is correct.");
            return;
        }

        result.TotalHttpObjects.Should().BeGreaterThan(0);
        long sumByCategory = result.HttpClientCount + result.HttpWebRequestCount + result.HttpWebResponseCount
            + result.HttpMessageHandlerCount + result.ServicePointCount
            + result.ActiveHandlerTrackingEntryCount + result.ExpiredHandlerTrackingEntryCount;
        sumByCategory.Should().Be(result.TotalHttpObjects);

        (result.HttpClientGen0Count + result.HttpClientGen1Count + result.HttpClientGen2Count).Should().Be(result.HttpClientCount);

        int sumByType = 0;
        foreach (HttpObjectTypeSummary t in result.ByType) sumByType += (int)Math.Min(t.Count, int.MaxValue);
        sumByType.Should().Be((int)Math.Min(result.TotalHttpObjects, int.MaxValue));

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].Count.Should().BeLessThanOrEqualTo(result.ByType[i - 1].Count);

        for (int i = 1; i < result.HandlerModules.Count; i++)
            result.HandlerModules[i].Count.Should().BeLessThanOrEqualTo(result.HandlerModules[i - 1].Count);

        _output.WriteLine(
            $"TotalHttpObjects={result.TotalHttpObjects} HttpClient={result.HttpClientCount} " +
            $"(Gen0={result.HttpClientGen0Count} Gen1={result.HttpClientGen1Count} Gen2={result.HttpClientGen2Count}) " +
            $"HttpWebRequest={result.HttpWebRequestCount} HttpWebResponse={result.HttpWebResponseCount} " +
            $"HttpMessageHandler={result.HttpMessageHandlerCount} ServicePoint={result.ServicePointCount} " +
            $"ActiveHandlerTrackingEntry={result.ActiveHandlerTrackingEntryCount} ExpiredHandlerTrackingEntry={result.ExpiredHandlerTrackingEntryCount} " +
            $"TopHttpInstances={result.TopHttpInstances.Count} HandlerModules={result.HandlerModules.Count}");
    }
}
