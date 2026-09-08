namespace DumpDetective.Core.Abstractions;

/// <summary>
/// "How much would become collectible if this thread exited?" — §12.2
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Backed by the
/// <c>RootStackThreadAttribution</c> section (which Stack-kind GC root belongs to which thread) and
/// <see cref="IDominatorTreeProvider"/> (exact retained bytes), cross-referenced once at open time.
/// </summary>
public interface IThreadRetentionProvider
{
    /// <summary>
    /// Returns <c>true</c> and the exact retained bytes for everything reachable only through
    /// <paramref name="osThreadId"/>'s own stack roots. An object also reachable from a second GC
    /// root (another thread's stack, a static field, a handle) is already correctly excluded — its
    /// dominator-tree immediate dominator isn't uniquely this thread's, so the same
    /// ancestor-exclusion aggregation §12.1 built for per-kind retained bytes (reused unmodified, not
    /// reimplemented here) never attributes it to this thread in the first place. Returns
    /// <c>false</c> if <paramref name="osThreadId"/> had no stack roots when
    /// the index was built, or if this provider is unavailable for the run (legacy cache.bin, Stage B
    /// not gated on, or the section failed to persist) — same "not an error" contract as
    /// <see cref="IDominatorTreeProvider.TryGetRetainedBytes"/>.
    /// </summary>
    bool TryGetRetainedBytesForThread(uint osThreadId, out ulong retainedBytes);
}
