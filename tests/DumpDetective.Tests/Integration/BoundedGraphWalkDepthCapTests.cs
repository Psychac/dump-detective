using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Tests.Integration.CacheDiscrepancies;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration;

public sealed class BoundedGraphWalkDepthCapTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void CollectRetainedObjects_ClampsRequestedDepthTo20()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        var roots = cache.GetOrBuildRoots(heap);
        roots.Should().NotBeEmpty();

        ulong rootAddress = roots[0].TargetAddr;

        // AbsoluteMaxDepth is enforced inside BoundedGraphWalk itself, so requesting a depth
        // far beyond 20 must produce exactly the same reachable set as requesting 20 directly.
        var atCap = BoundedGraphWalk.CollectRetainedObjects(heap, rootAddress, maxObjects: 50_000, maxDepth: 20);
        var wellBeyondCap = BoundedGraphWalk.CollectRetainedObjects(heap, rootAddress, maxObjects: 50_000, maxDepth: 5_000);

        wellBeyondCap.Count.Should().Be(atCap.Count);
    }
}
