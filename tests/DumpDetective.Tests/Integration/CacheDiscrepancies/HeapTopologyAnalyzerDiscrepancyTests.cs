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

public sealed class HeapTopologyAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task HeapTopologyAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        HeapTopologyAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        HeapTopologyDomainResult memResult = (HeapTopologyDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.HeapTopologyAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            HeapTopologyDomainResult diskResult = (HeapTopologyDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalSegments.Should().Be(memResult.TotalSegments);
            diskResult.TotalCommittedBytes.Should().Be(memResult.TotalCommittedBytes);
            diskResult.TotalUsedBytes.Should().Be(memResult.TotalUsedBytes);
            diskResult.TotalReservedBytes.Should().Be(memResult.TotalReservedBytes);
            diskResult.ReservationGapBytes.Should().Be(memResult.ReservationGapBytes);
            diskResult.SohSegmentCount.Should().Be(memResult.SohSegmentCount);
            diskResult.SohBytes.Should().Be(memResult.SohBytes);
            diskResult.LohSegmentCount.Should().Be(memResult.LohSegmentCount);
            diskResult.LohBytes.Should().Be(memResult.LohBytes);
            diskResult.PohSegmentCount.Should().Be(memResult.PohSegmentCount);
            diskResult.PohBytes.Should().Be(memResult.PohBytes);
            diskResult.FrozenSegmentCount.Should().Be(memResult.FrozenSegmentCount);
            diskResult.FrozenBytes.Should().Be(memResult.FrozenBytes);
            diskResult.FrozenPercent.Should().Be(memResult.FrozenPercent);
            diskResult.LohPercent.Should().Be(memResult.LohPercent);
            diskResult.PohPercent.Should().Be(memResult.PohPercent);
            diskResult.KindSummaries.Count.Should().Be(memResult.KindSummaries.Count);
            diskResult.PerLogicalHeapSummaries.Count.Should().Be(memResult.PerLogicalHeapSummaries.Count);
            (diskResult.TopPohTypes?.Count ?? 0).Should().Be(memResult.TopPohTypes?.Count ?? 0);
            (diskResult.TopFrozenTypes?.Count ?? 0).Should().Be(memResult.TopFrozenTypes?.Count ?? 0);
            (diskResult.TopSegmentsBySize?.Count ?? 0).Should().Be(memResult.TopSegmentsBySize?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
