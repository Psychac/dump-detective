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

public sealed class FinalizableObjectAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task FinalizableObjectAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        FinalizableObjectAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        FinalizableObjectDomainResult memResult = (FinalizableObjectDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            FinalizableObjectDomainResult diskResult = (FinalizableObjectDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalFinalizableObjects.Should().Be(memResult.TotalFinalizableObjects);
            diskResult.TotalFinalizableBytes.Should().Be(memResult.TotalFinalizableBytes);
            diskResult.Gen0Count.Should().Be(memResult.Gen0Count);
            diskResult.Gen1Count.Should().Be(memResult.Gen1Count);
            diskResult.Gen2Count.Should().Be(memResult.Gen2Count);
            diskResult.FinalizerQueueCount.Should().Be(memResult.FinalizerQueueCount);
            diskResult.FinalizerQueueRetainedBytes.Should().Be(memResult.FinalizerQueueRetainedBytes);
            diskResult.PotentialResurrectionDetected.Should().Be(memResult.PotentialResurrectionDetected);
            diskResult.TopFinalizableTypesByGen2Count.Count.Should().Be(memResult.TopFinalizableTypesByGen2Count.Count);
            diskResult.TopQueueEntriesByRetainedSize.Count.Should().Be(memResult.TopQueueEntriesByRetainedSize.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
