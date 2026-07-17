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

public sealed class GCHandleAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task GCHandleAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        GCHandleAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        GCHandleDomainResult memResult = (GCHandleDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.GCHandleAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            GCHandleDomainResult diskResult = (GCHandleDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalHandles.Should().Be(memResult.TotalHandles);
            diskResult.StrongLikeHandles.Should().Be(memResult.StrongLikeHandles);
            diskResult.WeakLikeHandles.Should().Be(memResult.WeakLikeHandles);
            diskResult.PinnedHandleTargets.Should().Be(memResult.PinnedHandleTargets);
            diskResult.PinnedRetainedBytes.Should().Be(memResult.PinnedRetainedBytes);
            (diskResult.HandlesByKind?.Count ?? 0).Should().Be(memResult.HandlesByKind?.Count ?? 0);
            (diskResult.TopTargetTypes?.Count ?? 0).Should().Be(memResult.TopTargetTypes?.Count ?? 0);
            (diskResult.TopPinnedTargetTypes?.Count ?? 0).Should().Be(memResult.TopPinnedTargetTypes?.Count ?? 0);
            (diskResult.TopPinnedObjectsBySize?.Count ?? 0).Should().Be(memResult.TopPinnedObjectsBySize?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
