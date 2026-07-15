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
using DumpDetective.Core.Models;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class TimerLeakAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task TimerLeakAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        TimerLeakAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        TimerLeakDomainResult memResult = (TimerLeakDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.TimerLeakAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            TimerLeakDomainResult diskResult = (TimerLeakDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TimersFound.Should().Be(memResult.TimersFound);
            diskResult.TotalTimers.Should().Be(memResult.TotalTimers);
            diskResult.ThreadingTimerCount.Should().Be(memResult.ThreadingTimerCount);
            diskResult.TimersTimerCount.Should().Be(memResult.TimersTimerCount);
            diskResult.TimerQueueTimerCount.Should().Be(memResult.TimerQueueTimerCount);
            diskResult.TimerHolderCount.Should().Be(memResult.TimerHolderCount);
            diskResult.OtherTimerCount.Should().Be(memResult.OtherTimerCount);
            diskResult.TotalBytes.Should().Be(memResult.TotalBytes);
            diskResult.ByType.Count.Should().Be(memResult.ByType.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
