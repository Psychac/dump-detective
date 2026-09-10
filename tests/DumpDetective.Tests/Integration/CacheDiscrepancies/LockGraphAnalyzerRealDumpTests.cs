using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the thread-domain quartet retyping
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md). The self-attached-heap
/// characterization test only exercises the current test host's own small thread/lock set; this
/// proves the same invariants against a large real dump — hundreds of threads, real contested
/// monitors, real deadlock-candidate detection.
/// </summary>
public sealed class LockGraphAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public LockGraphAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        int expectedHeldLocks = 0;
        foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
        {
            if (sb.IsMonitorHeld && sb.Object != 0)
                expectedHeldLocks++;
        }

        HeapAnalysisCache cache = new();
        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using LockGraphAnalyzerLegacyAdapter adapter = new();
        var result = (LockGraphDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Lock Graph Analysis");
        result.TotalHeldLocks.Should().Be(expectedHeldLocks);
        result.ContestedLockCount.Should().BeLessThanOrEqualTo(result.TotalHeldLocks);
        result.DeadlockCandidateCount.Should().Be(result.DeadlockCandidateDetails!.Count);

        for (int i = 1; i < result.ContestedLockDetails!.Count; i++)
            result.ContestedLockDetails[i].WaitingThreadCount.Should().BeLessThanOrEqualTo(result.ContestedLockDetails[i - 1].WaitingThreadCount);

        foreach (DeadlockCandidateSnapshot dc in result.DeadlockCandidateDetails)
            dc.LockObjectTypes.Count.Should().Be(dc.LockObjectAddresses.Count);

        _output.WriteLine(
            $"TotalHeldLocks={result.TotalHeldLocks:N0} Contested={result.ContestedLockCount:N0} " +
            $"MaxWaiters={result.MaxWaitersOnSingleLock:N0} DeadlockCandidates={result.DeadlockCandidateCount:N0} " +
            $"LocksWithOwnerAddress={result.LocksWithOwnerAddress:N0} UnresolvedOwner={result.UnresolvedOwnerCount:N0}");
    }
}
