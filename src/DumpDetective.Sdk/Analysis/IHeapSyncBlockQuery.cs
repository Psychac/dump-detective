using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One live sync block (a monitor lock taken via <c>lock</c>/<c>Monitor.Enter</c>). Backs
/// <c>LockGraphAnalyzer</c>'s <c>heap.EnumerateSyncBlocks()</c> usage, gated behind
/// <see cref="CapabilityVocabulary.RuntimeLocks"/> — a capability already declared in the
/// vocabulary but unconsumed until this surface. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <param name="HasHoldingThread">
/// Whether ClrMD reports any holding-thread address at all for this sync block (mirrors
/// <c>SyncBlock.HoldingThreadAddress != 0</c>) — distinct from <paramref name="HoldingOsThreadId"/>
/// being non-null, which additionally requires that address to have resolved to a live
/// <c>ClrThread</c>. A monitor can have a holding-thread address that no longer correlates to any
/// thread in <c>runtime.Threads</c> (stale/exited thread); collapsing these two facts into one
/// nullable field would make "no owner at all" indistinguishable from "owner address present but
/// unresolvable" — a real distinction the pre-retyping analyzer's own
/// <c>LocksWithOwnerAddress</c>/<c>UnresolvedOwnerCount</c> domain-result fields depend on.
/// </param>
public readonly record struct HeapSyncBlockRef(
    ulong ObjectAddress,
    int SyncBlockIndex,
    bool IsMonitorHeld,
    uint? HoldingOsThreadId,
    bool HasHoldingThread,
    /// <summary>Reentrancy count for the holding thread — mirrors ClrMD's <c>SyncBlock.RecursionCount</c>.
    /// Added 2026-09-11 for <c>LockGraphAnalyzer</c>'s retyping
    /// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).</summary>
    int RecursionCount = 0,
    /// <summary>Number of threads blocked waiting to acquire this monitor — mirrors ClrMD's
    /// <c>SyncBlock.WaitingThreadCount</c>. A lock is "contested" when this is greater than
    /// zero.</summary>
    int WaitingThreadCount = 0);

/// <summary>The <c>runtime.locks</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapSyncBlockQuery
{
    IEnumerable<HeapSyncBlockRef> EnumerateSyncBlocks();
}
