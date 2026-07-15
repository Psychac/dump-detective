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

public sealed class MemoryAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task MemoryAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        MemoryAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        MemoryDomainResult memResult = (MemoryDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.MemoryAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            MemoryDomainResult diskResult = (MemoryDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalBytes.Should().Be(memResult.TotalBytes);
            diskResult.LohBytes.Should().Be(memResult.LohBytes);
            diskResult.LohPercent.Should().Be(memResult.LohPercent);
            diskResult.TotalObjects.Should().Be(memResult.TotalObjects);
            diskResult.LohObjects.Should().Be(memResult.LohObjects);
            diskResult.LohThresholdBytes.Should().Be(memResult.LohThresholdBytes);
            diskResult.UniqueTypes.Should().Be(memResult.UniqueTypes);
            diskResult.SmallObjectCountPercent.Should().Be(memResult.SmallObjectCountPercent);
            diskResult.SmallObjectBytesPercent.Should().Be(memResult.SmallObjectBytesPercent);
            diskResult.ObjectsPerMb.Should().Be(memResult.ObjectsPerMb);
            diskResult.MemoryPressureScore.Should().Be(memResult.MemoryPressureScore);
            diskResult.TopTypes.Count.Should().Be(memResult.TopTypes.Count);
            (diskResult.SizeBucketHistogram?.Count ?? 0).Should().Be(memResult.SizeBucketHistogram?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
