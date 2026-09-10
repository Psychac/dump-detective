using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>runtime.locks</c> capability's sync-block surface, built for
/// <c>LockGraphAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// </summary>
/// <remarks>
/// Resolves <see cref="HeapSyncBlockRef.HoldingOsThreadId"/> from ClrMD's own
/// <c>SyncBlock.HoldingThreadAddress</c> (a dump-local <c>ClrThread</c> address) via a lazily-built
/// address → OS-thread-id map — deliberately not exposing the raw address itself, so the retyped
/// analyzer correlates lock ownership against <c>RuntimeThreadRef.Thread.OsThreadId</c> the same way
/// every other capability already keys threads, rather than reintroducing a raw dump-local handle at
/// the SDK boundary.
/// </remarks>
internal sealed class HeapSyncBlockQuery(ClrHeap heap) : IHeapSyncBlockQuery
{
    private Dictionary<ulong, uint>? _osThreadIdByAddress;

    public IEnumerable<HeapSyncBlockRef> EnumerateSyncBlocks()
    {
        _osThreadIdByAddress ??= BuildOsThreadIdByAddress();

        foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
        {
            uint? holdingOsThreadId = sb.HoldingThreadAddress != 0 && _osThreadIdByAddress.TryGetValue(sb.HoldingThreadAddress, out uint osThreadId)
                ? osThreadId
                : null;

            yield return new HeapSyncBlockRef(
                ObjectAddress: sb.Object,
                SyncBlockIndex: sb.Index,
                IsMonitorHeld: sb.IsMonitorHeld,
                HoldingOsThreadId: holdingOsThreadId,
                HasHoldingThread: sb.HoldingThreadAddress != 0,
                RecursionCount: sb.RecursionCount,
                WaitingThreadCount: sb.WaitingThreadCount);
        }
    }

    private Dictionary<ulong, uint> BuildOsThreadIdByAddress()
    {
        var map = new Dictionary<ulong, uint>();
        foreach (ClrThread thread in heap.Runtime.Threads)
        {
            if (thread.Address != 0)
                map[thread.Address] = thread.OSThreadId;
        }
        return map;
    }
}
