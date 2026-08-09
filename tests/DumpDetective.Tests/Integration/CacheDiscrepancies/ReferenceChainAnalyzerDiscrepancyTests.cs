using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class ReferenceChainAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task ReferenceChainAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        ReferenceChainAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        ReferenceChainDomainResult memResult = (ReferenceChainDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.ReferenceChainAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            ReferenceChainDomainResult diskResult = (ReferenceChainDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.AnalyzedSamples.Should().Be(memResult.AnalyzedSamples);
            diskResult.RetainedSamples.Should().Be(memResult.RetainedSamples);
            diskResult.RetainedPercent.Should().Be(memResult.RetainedPercent);
            diskResult.TraversalLimitedSamples.Should().Be(memResult.TraversalLimitedSamples);
            (diskResult.RetainedTypeNames?.Count ?? 0).Should().Be(memResult.RetainedTypeNames?.Count ?? 0);
            (diskResult.SampleReferenceChains?.Count ?? 0).Should().Be(memResult.SampleReferenceChains?.Count ?? 0);
            (diskResult.TopTypeSampleTraces?.Count ?? 0).Should().Be(memResult.TopTypeSampleTraces?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
