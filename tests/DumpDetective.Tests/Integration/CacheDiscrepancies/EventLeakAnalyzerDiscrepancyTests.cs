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

public sealed class EventLeakAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task EventLeakAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        EventLeakAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        EventLeakDomainResult memResult = (EventLeakDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.EventLeakAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            EventLeakDomainResult diskResult = (EventLeakDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalEventLeakInstances.Should().Be(memResult.TotalEventLeakInstances);
            diskResult.TotalSubscribers.Should().Be(memResult.TotalSubscribers);
            diskResult.StaticEventLeakCount.Should().Be(memResult.StaticEventLeakCount);
            diskResult.InstanceEventLeakCount.Should().Be(memResult.InstanceEventLeakCount);
            diskResult.TotalEventsScanned.Should().Be(memResult.TotalEventsScanned);
            diskResult.TotalPublisherInstances.Should().Be(memResult.TotalPublisherInstances);
            (diskResult.TopPublisherEventsBySubscribers?.Count ?? 0).Should().Be(memResult.TopPublisherEventsBySubscribers?.Count ?? 0);
            (diskResult.TopLeakGroups?.Count ?? 0).Should().Be(memResult.TopLeakGroups?.Count ?? 0);
            (diskResult.TopLeakInstances?.Count ?? 0).Should().Be(memResult.TopLeakInstances?.Count ?? 0);
            (diskResult.TopPublisherEvents?.Count ?? 0).Should().Be(memResult.TopPublisherEvents?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
