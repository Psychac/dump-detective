using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One live sync block (a monitor lock taken via <c>lock</c>/<c>Monitor.Enter</c>). Backs
/// <c>LockGraphAnalyzer</c>'s <c>heap.EnumerateSyncBlocks()</c> usage, gated behind
/// <see cref="CapabilityVocabulary.RuntimeLocks"/> — a capability already declared in the
/// vocabulary but unconsumed until this surface. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public readonly record struct HeapSyncBlockRef(ulong ObjectAddress, int SyncBlockIndex, bool IsMonitorHeld, uint? HoldingOsThreadId = null);

/// <summary>The <c>runtime.locks</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapSyncBlockQuery
{
    IEnumerable<HeapSyncBlockRef> EnumerateSyncBlocks();
}
