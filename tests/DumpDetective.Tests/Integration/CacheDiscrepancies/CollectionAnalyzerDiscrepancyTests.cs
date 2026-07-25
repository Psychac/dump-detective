using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class CollectionAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task CollectionAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        CollectionDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache);
        string freshDumpPath = dumpPath + ".freshdiskcheck.CollectionAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            CollectionDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache);
            diskResult.TotalCollections.Should().Be(memResult.TotalCollections);
            diskResult.Dictionaries.Should().Be(memResult.Dictionaries);
            diskResult.Lists.Should().Be(memResult.Lists);
            diskResult.ArrayLists.Should().Be(memResult.ArrayLists);
            diskResult.Stacks.Should().Be(memResult.Stacks);
            diskResult.SortedLists.Should().Be(memResult.SortedLists);
            diskResult.SortedSets.Should().Be(memResult.SortedSets);
            diskResult.HashSets.Should().Be(memResult.HashSets);
            diskResult.Queues.Should().Be(memResult.Queues);
            diskResult.TotalWastedMemory.Should().Be(memResult.TotalWastedMemory);
            diskResult.WastefulCollectionCount.Should().Be(memResult.WastefulCollectionCount);
            (diskResult.TopWastefulCollections?.Count ?? 0).Should().Be(memResult.TopWastefulCollections?.Count ?? 0);
            (diskResult.WasteCountsByKind?.Count ?? 0).Should().Be(memResult.WasteCountsByKind?.Count ?? 0);
            (diskResult.GenerationBreakdown?.Count ?? 0).Should().Be(memResult.GenerationBreakdown?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    private static async Task<CollectionDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache };
        AnalysisPipeline pipeline = new([new CollectionAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<CollectionDomainResult>()!;
    }
}
