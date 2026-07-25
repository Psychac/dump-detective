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

public sealed class CrashAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task CrashAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        CrashDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache, analysisOptions);
        string freshDumpPath = dumpPath + ".freshdiskcheck.CrashAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            CrashDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache, analysisOptions);
            diskResult.TotalExceptions.Should().Be(memResult.TotalExceptions);
            diskResult.ActiveExceptions.Should().Be(memResult.ActiveExceptions);
            diskResult.InferredTraceCount.Should().Be(memResult.InferredTraceCount);
            diskResult.ExceptionTypeCounts.Count.Should().Be(memResult.ExceptionTypeCounts.Count);
            diskResult.ActiveExceptionTypeCounts.Count.Should().Be(memResult.ActiveExceptionTypeCounts.Count);
            (diskResult.TopCrashThreadCandidates?.Count ?? 0).Should().Be(memResult.TopCrashThreadCandidates?.Count ?? 0);
            (diskResult.TopExceptionInstances?.Count ?? 0).Should().Be(memResult.TopExceptionInstances?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    // A fresh CrashAnalyzer instance is used per call since the analyzer now carries instance
    // accumulator state primed by BeforeHeapIndexScan.
    private static async Task<CrashDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache, AnalysisOptions analysisOptions)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };
        AnalysisPipeline pipeline = new([new CrashAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<CrashDomainResult>()!;
    }
}
