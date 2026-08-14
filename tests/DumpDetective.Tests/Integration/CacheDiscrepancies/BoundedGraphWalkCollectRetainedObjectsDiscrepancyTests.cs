using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 4 validation for docs/cache/cache-architecture.md: the correctness oracle for
/// <see cref="BoundedGraphWalk.CollectRetainedObjects"/>'s free-tuple capture — the
/// <c>(MethodTable, Size)</c> it now returns per address must agree with a live
/// <c>heap.GetObject(address)</c> resolution, and <see cref="StaticRootLeakDetector"/>'s refactored
/// consumption of that dictionary must still run end-to-end without exceptions.
/// </summary>
public sealed class BoundedGraphWalkCollectRetainedObjectsDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public BoundedGraphWalkCollectRetainedObjectsDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void CollectRetainedObjects_CapturedMetadata_AgreesWithLiveHeapGetObject()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        IReadOnlyList<RootRecord> roots = cache.GetOrBuildRoots(heap);
        roots.Should().NotBeEmpty();

        // Pick a handful of roots to get a reasonably large, varied retained set rather than
        // relying on whichever root happens to be first.
        var candidateRoots = roots.Take(50).Select(r => r.TargetAddr).Distinct().ToList();

        int checkedCount = 0;
        int mismatches = 0;
        int unresolvedFrontier = 0;
        var mismatchDetails = new List<string>();

        foreach (ulong rootAddress in candidateRoots)
        {
            if (rootAddress == 0) continue;

            var retained = BoundedGraphWalk.CollectRetainedObjects(heap, rootAddress, out bool wasCapped, maxObjects: 5_000);

            foreach (var (address, (methodTable, size)) in retained)
            {
                if (methodTable == 0)
                {
                    // Per CollectRetainedObjects' contract: either genuinely invalid, or (only
                    // possible when wasCapped) a frontier node never dequeued.
                    unresolvedFrontier++;
                    continue;
                }

                checkedCount++;
                ClrObject live = heap.GetObject(address);
                ulong liveMt = live.IsValid ? (live.Type?.MethodTable ?? 0) : 0;
                ulong liveSize = live.IsValid ? live.Size : 0;

                if (methodTable != liveMt || size != liveSize)
                {
                    mismatches++;
                    if (mismatchDetails.Count < 20)
                        mismatchDetails.Add($"0x{address:X}: got MT=0x{methodTable:X} Size={size}, expected MT=0x{liveMt:X} Size={liveSize} (live.IsValid={live.IsValid})");
                }
            }
        }

        _output.WriteLine($"roots checked: {candidateRoots.Count}, addresses checked: {checkedCount}, unresolved/frontier: {unresolvedFrontier}, mismatches: {mismatches}");
        foreach (string d in mismatchDetails)
            _output.WriteLine(d);

        mismatches.Should().Be(0);
        checkedCount.Should().BeGreaterThan(0);
    }

    [DiscrepancyFact]
    public async Task StaticRootLeakDetector_RunsEndToEnd_AfterFreeTupleRefactor()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        StaticRootLeakDetector analyzer = new();
        var context = new AnalysisContext { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions() };

        AnalyzerDomainResult result = await analyzer.AnalyzeAsync(context, CancellationToken.None);

        var staticRootResult = result.Should().BeOfType<StaticRootDomainResult>().Subject;
        _output.WriteLine($"RootCount={staticRootResult.RootCount}, TotalRetainedBytes={staticRootResult.TotalRetainedBytes}, TopRoots={staticRootResult.TopRootsByRetainedBytes?.Count ?? 0}");

        staticRootResult.RootCount.Should().BeGreaterThanOrEqualTo(0);
        staticRootResult.TopRootsByRetainedBytes.Should().NotBeNull();

        foreach (StaticRootSnapshot snapshot in staticRootResult.TopRootsByRetainedBytes!)
        {
            snapshot.TotalMemoryImpact.Should().BeGreaterThanOrEqualTo(0);
            snapshot.ObjectsKeptAlive.Should().BeGreaterThanOrEqualTo(0);
        }
    }
}
