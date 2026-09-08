using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class RootSetCacheDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void RootSetCache_DiskVsMemoryVsLiveWalk_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        var memRoots = memCache.GetOrBuildValidRoots(heap);
        var memStaticRooted = memCache.GetStaticRootedAddresses(heap);

        // No PrebuildHeapIndex call at all: RootSetCache has no disk/memory index to
        // read from, so GetOrBuildRoots must fall back to a live heap.EnumerateRoots() walk.
        HeapAnalysisCache liveWalkCache = new();
        var liveWalkRoots = liveWalkCache.GetOrBuildRoots(heap);

        string freshDumpPath = dumpPath + ".rootsetcachediscrepancy";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            var diskRoots = diskCache.GetOrBuildValidRoots(heap);
            var diskStaticRooted = diskCache.GetStaticRootedAddresses(heap);

            diskRoots.Count.Should().Be(memRoots.Count);
            diskStaticRooted.Count.Should().Be(memStaticRooted.Count);
            liveWalkRoots.Count.Should().Be(memRoots.Count);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
