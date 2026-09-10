using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the thread-domain quartet retyping
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md): proves
/// <see cref="LockGraphAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic against a
/// self-attached process's real sync-block table, cross-checked against ClrMD ground truth — sync
/// blocks and thread state can't be hand-crafted via reflection injection any more than other
/// batches' segment/root/handle data could. Complements
/// <c>LockGraphAnalyzerLiveHeapTests</c>' real-contention/deadlock scenarios (already updated to run
/// through the adapter) and <c>AnalysisPipelineTests</c>' shared-scan gate.
/// </summary>
public sealed class LockGraphAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_MatchesClrMdGroundTruth()
    {
        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        int expectedHeldLocks = 0, expectedContestedLocks = 0;
        foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
        {
            if (!sb.IsMonitorHeld || sb.Object == 0) continue;
            expectedHeldLocks++;
            if (sb.WaitingThreadCount > 0) expectedContestedLocks++;
        }

        HeapAnalysisCache cache = new();
        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using LockGraphAnalyzerLegacyAdapter adapter = new();
        var result = (LockGraphDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Lock Graph Analysis");
        result.Category.Should().Be("Locks");
        result.TotalHeldLocks.Should().Be(expectedHeldLocks);
        result.ContestedLockCount.Should().Be(expectedContestedLocks);

        for (int i = 1; i < result.ContestedLockDetails!.Count; i++)
            result.ContestedLockDetails[i].WaitingThreadCount.Should().BeLessThanOrEqualTo(result.ContestedLockDetails[i - 1].WaitingThreadCount);

        result.MaxWaitersOnSingleLock.Should().Be(
            result.ContestedLockDetails.Count > 0 ? result.ContestedLockDetails[0].WaitingThreadCount : 0);

        (result.LocksWithOwnerAddress - result.UnresolvedOwnerCount).Should().BeGreaterThanOrEqualTo(0);
        result.DeadlockCandidateCount.Should().Be(result.DeadlockCandidateDetails!.Count);

        foreach (DeadlockCandidateSnapshot dc in result.DeadlockCandidateDetails)
        {
            dc.LockObjectTypes.Count.Should().Be(dc.LockObjectAddresses.Count);
            dc.OwnerThreadFrames!.Count.Should().BeLessThanOrEqualTo(3);
        }
    }
}
