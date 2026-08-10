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

public sealed class WeakReferenceAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task WeakReferenceAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        WeakReferenceAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        WeakReferenceDomainResult memResult = (WeakReferenceDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.WeakReferenceAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            WeakReferenceDomainResult diskResult = (WeakReferenceDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalWeakHandles.Should().Be(memResult.TotalWeakHandles);
            diskResult.AliveWeakTargets.Should().Be(memResult.AliveWeakTargets);
            diskResult.DeadWeakTargets.Should().Be(memResult.DeadWeakTargets);
            diskResult.DeadTargetRatio.Should().Be(memResult.DeadTargetRatio);
            diskResult.WeakReferenceObjectCount.Should().Be(memResult.WeakReferenceObjectCount);
            diskResult.WeakReferenceObjectBytes.Should().Be(memResult.WeakReferenceObjectBytes);
            diskResult.StaleWrapperCount.Should().Be(memResult.StaleWrapperCount);
            diskResult.DependentHandleDeadKeyCount.Should().Be(memResult.DependentHandleDeadKeyCount);
            diskResult.ScanCapped.Should().Be(memResult.ScanCapped);
            diskResult.PhaseBFallbackUsed.Should().Be(memResult.PhaseBFallbackUsed);
            diskResult.PhaseBSkipped.Should().Be(memResult.PhaseBSkipped);
            diskResult.WeakHandleKinds.Count.Should().Be(memResult.WeakHandleKinds.Count);
            diskResult.TopWeakTargetTypes.Count.Should().Be(memResult.TopWeakTargetTypes.Count);
            diskResult.TopStaleWrapperHolderTypes.Count.Should().Be(memResult.TopStaleWrapperHolderTypes.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
