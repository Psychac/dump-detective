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

public sealed class DbConnectionAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task DbConnectionAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        DbConnectionDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache);
        string freshDumpPath = dumpPath + ".freshdiskcheck.DbConnectionAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            DbConnectionDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache);
            diskResult.ConnectionsFound.Should().Be(memResult.ConnectionsFound);
            diskResult.TotalConnections.Should().Be(memResult.TotalConnections);
            diskResult.OpenConnections.Should().Be(memResult.OpenConnections);
            diskResult.ClosedConnections.Should().Be(memResult.ClosedConnections);
            diskResult.OtherConnections.Should().Be(memResult.OtherConnections);
            diskResult.StateScanCapped.Should().Be(memResult.StateScanCapped);
            diskResult.ByType.Count.Should().Be(memResult.ByType.Count);
            diskResult.TopOpenConnections.Count.Should().Be(memResult.TopOpenConnections.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    private static async Task<DbConnectionDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache };
        AnalysisPipeline pipeline = new([new DbConnectionAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<DbConnectionDomainResult>()!;
    }
}
