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

public sealed class ThreadAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task ThreadAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        ThreadAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        ThreadDomainResult memResult = (ThreadDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck.ThreadAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            ThreadDomainResult diskResult = (ThreadDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalThreadCount.Should().Be(memResult.TotalThreadCount);
            diskResult.AliveThreadCount.Should().Be(memResult.AliveThreadCount);
            diskResult.InactiveThreadCount.Should().Be(memResult.InactiveThreadCount);
            diskResult.GcThreadCount.Should().Be(memResult.GcThreadCount);
            diskResult.BlockedThreadCount.Should().Be(memResult.BlockedThreadCount);
            diskResult.LockHoldingThreadCount.Should().Be(memResult.LockHoldingThreadCount);
            diskResult.ThreadsWithActiveExceptionsCount.Should().Be(memResult.ThreadsWithActiveExceptionsCount);
            diskResult.BackgroundThreadCount.Should().Be(memResult.BackgroundThreadCount);
            diskResult.ThreadPoolWorkerCount.Should().Be(memResult.ThreadPoolWorkerCount);
            diskResult.FinalizerThreadCount.Should().Be(memResult.FinalizerThreadCount);
            diskResult.FinalizerThreadBlocked.Should().Be(memResult.FinalizerThreadBlocked);
            diskResult.FinalizerLockCount.Should().Be(memResult.FinalizerLockCount);
            diskResult.AsyncChainThreadCount.Should().Be(memResult.AsyncChainThreadCount);
            diskResult.MaxAsyncChainDepth.Should().Be(memResult.MaxAsyncChainDepth);
            diskResult.SampledSnapshotCount.Should().Be(memResult.SampledSnapshotCount);
            diskResult.CapturedSnapshotCount.Should().Be(memResult.CapturedSnapshotCount);
            diskResult.SamplingCapacity.Should().Be(memResult.SamplingCapacity);
            diskResult.SamplingSeed.Should().Be(memResult.SamplingSeed);
            diskResult.WaitPatternBreakdown.Count.Should().Be(memResult.WaitPatternBreakdown.Count);
            (diskResult.ThreadStateDistribution?.Count ?? 0).Should().Be(memResult.ThreadStateDistribution?.Count ?? 0);
            (diskResult.AppDomainDistribution?.Count ?? 0).Should().Be(memResult.AppDomainDistribution?.Count ?? 0);
            (diskResult.GcModeDistribution?.Count ?? 0).Should().Be(memResult.GcModeDistribution?.Count ?? 0);
            (diskResult.TopLockedThreads?.Count ?? 0).Should().Be(memResult.TopLockedThreads?.Count ?? 0);
            (diskResult.TopBlockedThreads?.Count ?? 0).Should().Be(memResult.TopBlockedThreads?.Count ?? 0);
            (diskResult.ThreadsWithActiveExceptions?.Count ?? 0).Should().Be(memResult.ThreadsWithActiveExceptions?.Count ?? 0);
            (diskResult.TopStackHotspots?.Count ?? 0).Should().Be(memResult.TopStackHotspots?.Count ?? 0);
            (diskResult.TopActiveThreadHotspots?.Count ?? 0).Should().Be(memResult.TopActiveThreadHotspots?.Count ?? 0);
            (diskResult.SampledThreads?.Count ?? 0).Should().Be(memResult.SampledThreads?.Count ?? 0);
            (diskResult.FinalizerFrames?.Count ?? 0).Should().Be(memResult.FinalizerFrames?.Count ?? 0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
