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

public sealed class AllocationPatternAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task AllocationPatternAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        AllocationPatternAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        AllocationPatternDomainResult memResult = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.AllocationPatternAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            AllocationPatternDomainResult diskResult = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.Gen0CountPct.Should().Be(memResult.Gen0CountPct);
            diskResult.Gen1CountPct.Should().Be(memResult.Gen1CountPct);
            diskResult.Gen2CountPct.Should().Be(memResult.Gen2CountPct);
            diskResult.LohCountPct.Should().Be(memResult.LohCountPct);
            diskResult.Gen0SizePct.Should().Be(memResult.Gen0SizePct);
            diskResult.Gen1SizePct.Should().Be(memResult.Gen1SizePct);
            diskResult.Gen2SizePct.Should().Be(memResult.Gen2SizePct);
            diskResult.LohSizePct.Should().Be(memResult.LohSizePct);
            diskResult.Profile.Should().Be(memResult.Profile);
            diskResult.GCPressure.Should().Be(memResult.GCPressure);
            diskResult.PromotionPressureScore.Should().Be(memResult.PromotionPressureScore);
            diskResult.TopTransientTypes.Count.Should().Be(memResult.TopTransientTypes.Count);
            diskResult.TopShortishTypes.Count.Should().Be(memResult.TopShortishTypes.Count);
            diskResult.TopLongLivedTypes.Count.Should().Be(memResult.TopLongLivedTypes.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
