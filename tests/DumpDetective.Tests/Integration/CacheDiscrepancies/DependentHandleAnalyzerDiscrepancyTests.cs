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

public sealed class DependentHandleAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task DependentHandleAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        DependentHandleAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        DependentHandleDomainResult memResult = (DependentHandleDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            DependentHandleDomainResult diskResult = (DependentHandleDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.DependentHandleCount.Should().Be(memResult.DependentHandleCount);
            diskResult.ResolvedEdgeCount.Should().Be(memResult.ResolvedEdgeCount);
            diskResult.UnresolvedTargetCount.Should().Be(memResult.UnresolvedTargetCount);
            diskResult.UnresolvedPercent.Should().Be(memResult.UnresolvedPercent);
            (diskResult.TopSourceTypes?.Count ?? 0).Should().Be(memResult.TopSourceTypes?.Count ?? 0);
            (diskResult.TopTargetTypes?.Count ?? 0).Should().Be(memResult.TopTargetTypes?.Count ?? 0);
            (diskResult.TopSourceTargetEdges?.Count ?? 0).Should().Be(memResult.TopSourceTargetEdges?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
