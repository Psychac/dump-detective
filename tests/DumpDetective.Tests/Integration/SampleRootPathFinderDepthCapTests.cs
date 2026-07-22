using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Tests.Integration.CacheDiscrepancies;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration;

public sealed class SampleRootPathFinderDepthCapTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void TryFindSampleRootPath_ClampsRequestedDepthTo20()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        var roots = cache.GetOrBuildValidRoots(heap);
        roots.Should().NotBeEmpty();

        ulong targetAddress = roots[^1].Address;

        // AbsoluteMaxDepth is enforced inside SampleRootPathFinder itself, so requesting a depth
        // far beyond 20 must produce exactly the same result as requesting 20 directly.
        var atCap = SampleRootPathFinder.TryFindSampleRootPath(heap, roots, targetAddress, maxPathDepth: 20);
        var wellBeyondCap = SampleRootPathFinder.TryFindSampleRootPath(heap, roots, targetAddress, maxPathDepth: 5_000);

        wellBeyondCap.Path.Should().Be(atCap.Path);
        wellBeyondCap.Truncated.Should().Be(atCap.Truncated);
    }
}
