using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using FluentAssertions;
using Xunit;
using DumpDetective.Core.Models;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class WcfChannelAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task WcfChannelAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        WcfChannelDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache);
        string freshDumpPath = dumpPath + ".freshdiskcheck.WcfChannelAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            WcfChannelDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache);
            diskResult.WcfPresent.Should().Be(memResult.WcfPresent);
            diskResult.TotalChannels.Should().Be(memResult.TotalChannels);
            diskResult.OpeningChannels.Should().Be(memResult.OpeningChannels);
            diskResult.OpenedChannels.Should().Be(memResult.OpenedChannels);
            diskResult.FaultedChannels.Should().Be(memResult.FaultedChannels);
            diskResult.ClosingChannels.Should().Be(memResult.ClosingChannels);
            diskResult.ClosedChannels.Should().Be(memResult.ClosedChannels);
            diskResult.OtherChannels.Should().Be(memResult.OtherChannels);
            diskResult.StateScanCapped.Should().Be(memResult.StateScanCapped);
            diskResult.ByType.Count.Should().Be(memResult.ByType.Count);
            diskResult.TopFaultedChannels.Count.Should().Be(memResult.TopFaultedChannels.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    private static async Task<WcfChannelDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache };
        AnalysisPipeline pipeline = new([new WcfChannelAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<WcfChannelDomainResult>()!;
    }
}
