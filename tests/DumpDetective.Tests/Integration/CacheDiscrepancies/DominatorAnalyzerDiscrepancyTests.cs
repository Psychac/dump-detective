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

public sealed class DominatorAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task DominatorAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        DominatorAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Memory);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        DominatorDomainResult memResult = (DominatorDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Disk);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            DominatorDomainResult diskResult = (DominatorDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.CandidateCount.Should().Be(memResult.CandidateCount);
            diskResult.AnalyzedCount.Should().Be(memResult.AnalyzedCount);
            diskResult.TotalEstimatedRetainedBytes.Should().Be(memResult.TotalEstimatedRetainedBytes);
            diskResult.HeuristicOnly.Should().Be(memResult.HeuristicOnly);
            diskResult.MaxBreadth.Should().Be(memResult.MaxBreadth);
            diskResult.MaxDepth.Should().Be(memResult.MaxDepth);
            diskResult.TopDominatorTypes.Count.Should().Be(memResult.TopDominatorTypes.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
