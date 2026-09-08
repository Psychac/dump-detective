using System.Linq;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// P1-4 (docs/analysis/phase1/lock-graph-analyzer-audit.md): exercises <see cref="LockGraphAnalyzer"/>
/// against real monitor contention on this test process's own heap/threads instead of only via the
/// real-dump discrepancy path. A thin lock only inflates to a <see cref="SyncBlock"/> under genuine
/// contention (per the audit's documented ClrMD limitation), so these scenarios use real background
/// threads and real <c>lock</c> statements rather than reflection-seeded state.
///
/// Does not cover the "unresolved owner" path (a <c>SyncBlock</c> whose holding thread has exited
/// without releasing it) — modern .NET has no supported way to terminate a managed thread without
/// unwinding its <c>finally</c> blocks (no <c>Thread.Abort</c>), so that state can't be produced from
/// live managed test code; it only occurs in real crash dumps.
/// </summary>
public sealed class LockGraphAnalyzerLiveHeapTests
{
    [Fact]
    public async Task Analyze_DetectsContestedLock_WhenSecondThreadBlocksOnHeldMonitor()
    {
        object lockObj = new();
        var holderEntered = new ManualResetEventSlim(false);
        var releaseHolder = new ManualResetEventSlim(false);
        var waiterStarted = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            lock (lockObj)
            {
                holderEntered.Set();
                releaseHolder.Wait();
            }
        })
        { IsBackground = true };
        holder.Start();
        holderEntered.Wait();

        var waiter = new Thread(() =>
        {
            waiterStarted.Set();
            lock (lockObj) { }
        })
        { IsBackground = true };
        waiter.Start();
        waiterStarted.Wait();

        try
        {
            uint holderId = (uint)holder.ManagedThreadId;
            LockGraphDomainResult result = await AnalyzeUntilAsync(r =>
                r.ContestedLockDetails is { Count: > 0 } details &&
                details.Any(cl => cl.OwnerManagedThreadId == holderId && cl.WaitingThreadCount > 0));

            ContestedLockSnapshot contended = result.ContestedLockDetails!
                .Single(cl => cl.OwnerManagedThreadId == holderId);
            contended.WaitingThreadCount.Should().BeGreaterThanOrEqualTo(1);
            contended.ObjectTypeName.Should().Be(typeof(object).FullName);
        }
        finally
        {
            releaseHolder.Set();
            holder.Join(TimeSpan.FromSeconds(5));
            waiter.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Analyze_FlagsDeadlockCandidate_WhenThreadHoldsInflatedLockAndBlocksEnteringAnother()
    {
        object lockA = new();
        object lockB = new();
        var bHoldsLockB = new ManualResetEventSlim(false);
        var releaseB = new ManualResetEventSlim(false);
        var aHoldsLockA = new ManualResetEventSlim(false);
        var releaseA = new ManualResetEventSlim(false);
        var proberStarted = new ManualResetEventSlim(false);

        var threadB = new Thread(() =>
        {
            lock (lockB)
            {
                bHoldsLockB.Set();
                releaseB.Wait();
            }
        })
        { IsBackground = true };
        threadB.Start();
        bHoldsLockB.Wait();

        var threadA = new Thread(() =>
        {
            lock (lockA)
            {
                aHoldsLockA.Set();
                releaseA.Wait(); // held until the prober below has forced lockA to inflate
                lock (lockB) { } // blocks: lockB is held by threadB -> deadlock-candidate heuristic
            }
        })
        { IsBackground = true };
        threadA.Start();
        aHoldsLockA.Wait();

        // A held, uncontended lock stays a thin lock and is invisible to EnumerateSyncBlocks (per
        // the audit's documented limitation), so force lockA to inflate via real contention before
        // letting threadA proceed to nest into lockB.
        var prober = new Thread(() =>
        {
            proberStarted.Set();
            lock (lockA) { }
        })
        { IsBackground = true };
        prober.Start();
        proberStarted.Wait();

        try
        {
            uint threadAId = (uint)threadA.ManagedThreadId;

            await AnalyzeUntilAsync(r =>
                r.ContestedLockDetails is { Count: > 0 } details &&
                details.Any(cl => cl.OwnerManagedThreadId == threadAId && cl.WaitingThreadCount > 0));

            releaseA.Set();

            LockGraphDomainResult result = await AnalyzeUntilAsync(r =>
                r.DeadlockCandidateDetails is { Count: > 0 } details &&
                details.Any(dc => dc.ManagedThreadId == threadAId));

            DeadlockCandidateSnapshot candidate = result.DeadlockCandidateDetails!
                .Single(dc => dc.ManagedThreadId == threadAId);
            candidate.LockObjectTypes.Should().Contain(typeof(object).FullName);
            candidate.BlockedAtFrame.Should().NotBeNullOrEmpty();
        }
        finally
        {
            releaseA.Set();
            releaseB.Set();
            threadA.Join(TimeSpan.FromSeconds(5));
            threadB.Join(TimeSpan.FromSeconds(5));
            prober.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Analyze_ReportsNoContentionOrCandidates_ForHeldButUncontendedLock()
    {
        object lockObj = new();
        var holderEntered = new ManualResetEventSlim(false);
        var releaseHolder = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            lock (lockObj)
            {
                holderEntered.Set();
                releaseHolder.Wait();
            }
        })
        { IsBackground = true };
        holder.Start();
        holderEntered.Wait();

        try
        {
            uint holderId = (uint)holder.ManagedThreadId;

            // No waiter ever attempts lockObj, so it never inflates to a SyncBlock: the holder should
            // not appear as a contested-lock owner or a deadlock candidate.
            LockGraphDomainResult result = await AnalyzeOnceAsync();

            (result.ContestedLockDetails ?? [])
                .Should().NotContain(cl => cl.OwnerManagedThreadId == holderId);
            (result.DeadlockCandidateDetails ?? [])
                .Should().NotContain(dc => dc.ManagedThreadId == holderId);
        }
        finally
        {
            releaseHolder.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task<LockGraphDomainResult> AnalyzeOnceAsync()
    {
        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        var context = new AnalysisContext { Runtime = runtime, Cache = new HeapAnalysisCache() };
        LockGraphAnalyzer analyzer = new();
        return (LockGraphDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);
    }

    private static async Task<LockGraphDomainResult> AnalyzeUntilAsync(
        Func<LockGraphDomainResult, bool> isReady, int maxAttempts = 40, int delayMs = 50)
    {
        LockGraphDomainResult? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            last = await AnalyzeOnceAsync();
            if (isReady(last))
                return last;

            await Task.Delay(delayMs);
        }

        throw new InvalidOperationException(
            $"Expected lock graph condition was not observed after {maxAttempts} attempts. " +
            $"Last result: ContestedLocks={last!.ContestedLockCount}, DeadlockCandidates={last.DeadlockCandidateCount}.");
    }
}
