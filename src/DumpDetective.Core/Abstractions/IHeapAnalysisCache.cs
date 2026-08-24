using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public interface IHeapAnalysisCache
{
    long ObjectScanCount { get; }
    long CacheHits { get; }
    long CacheMisses { get; }
    // Dump size tier determined at index prebuild time. Used to tune IO buffers and sampling.
    DumpSizeTier SizeTier { get; }

    HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap);

    /// <summary>
    /// Addresses targeted by a <c>PinnedHandle</c> or <c>AsyncPinnedHandle</c> GC root. Backed by
    /// the same Phase-1 disk root index as <see cref="GetStaticRootedAddresses"/> (falling back to
    /// a live root walk in memory mode) — no extra heap or handle scan.
    /// </summary>
    HashSet<ulong> GetPinnedRootedAddresses(ClrHeap heap);
    Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> GetStaticFieldsByRootAddress(ClrHeap heap);

    /// <summary>
    /// Resolves a <c>Stack</c>-kind root's owning method (not an exact local-variable name — see
    /// docs/analysis/root-field-name-index-plan.md Mechanism B) by correlating its stack-slot
    /// address against the thread's frame ranges. Lazily builds and caches a per-thread map on
    /// first call; intended for the small set of top-severity <c>Stack</c> findings a report
    /// actually surfaces, not every stack root in the dump.
    /// </summary>
    bool TryResolveStackFrameOwner(ClrHeap heap, ulong rootAddr, out string ownerType, out string methodName);
    Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap);
    ulong? GetSampleInstanceAddress(string typeName);
    IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap);
    IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> GetOrBuildRootTriples(ClrHeap heap);
    int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount);
    bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable);
    IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples();

    /// <summary>
    /// Resolves <paramref name="address"/>'s <c>(MethodTable, Size)</c> via the disk-backed
    /// <c>SegmentIndex</c> point lookup when available (see docs/cache/cache-architecture.md),
    /// falling back to a live <paramref name="heap"/>.GetObject resolution when the disk index or its
    /// <c>SegmentIndex</c> section is unavailable (in-memory mode, old cache, aborted satellite
    /// write). Callers never need to branch on backing mode. Returns <c>false</c> — an expected
    /// outcome, not an error — when <paramref name="address"/> isn't a live object.
    /// </summary>
    bool TryGetObjectMetadata(ClrHeap heap, ulong address, out ulong methodTable, out ulong size);

    /// <summary>
    /// Returns the shared "who points at this object?" provider backed by the disk-backed
    /// reverse-reference index, or <c>null</c> when unavailable (in-memory mode, skipped build,
    /// pre-v4 cache, or missing sections). See <see cref="IBackwardReferenceProvider"/>.
    /// </summary>
    IBackwardReferenceProvider? TryGetReverseIndexProvider();

    /// <summary>
    /// Returns the shared "what does this object point at?" provider backed by the disk-backed
    /// forward-reference index, or <c>null</c> when unavailable (in-memory mode, skipped build via
    /// <c>DD_SKIP_FORWARD_INDEX_BUILD=1</c>, or missing sections). See
    /// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D5.
    /// </summary>
    IForwardReferenceProvider? TryGetForwardIndexProvider();

    /// <summary>
    /// Returns the shared "is this object reachable from a GC root?" provider backed by the
    /// disk-backed <c>DominatorReachableAddresses</c> section, or <c>null</c> when unavailable
    /// (in-memory mode, the reverse-edge index build was skipped so Stage A's walk never ran, or
    /// the section is missing). See
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §5.
    /// </summary>
    IReachableAddressProvider? TryGetReachableAddressProvider();

    /// <summary>
    /// Returns the shared "what would freeing this object free?" provider backed by the disk-backed
    /// exact dominator tree, or <c>null</c> when unavailable (in-memory mode, Stage B not gated on
    /// for this run, a legacy pre-Stage-B cache.bin, or Stage B failed to persist). See
    /// <see cref="IDominatorTreeProvider"/>.
    /// </summary>
    IDominatorTreeProvider? TryGetDominatorTreeProvider();

    /// <summary>
    /// Returns the shared "how much would become collectible if this thread exited?" provider, or
    /// <c>null</c> when unavailable (in-memory mode, the dominator tree itself unavailable, a legacy
    /// pre-§12.2 cache.bin, or the section failed to persist). See
    /// <see cref="IThreadRetentionProvider"/> and docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §12.2.
    /// </summary>
    IThreadRetentionProvider? TryGetThreadRetentionProvider();
}
