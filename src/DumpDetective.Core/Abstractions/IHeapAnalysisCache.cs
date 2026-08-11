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
    Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap);
    ulong? GetSampleInstanceAddress(string typeName);
    IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap);
    int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount);
    bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable);
    IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples();

    /// <summary>
    /// Returns the shared "who points at this object?" provider backed by the disk-backed
    /// reverse-reference index, or <c>null</c> when unavailable (in-memory mode, skipped build,
    /// pre-v4 cache, or missing sections). See <see cref="IBackwardReferenceProvider"/>.
    /// </summary>
    IBackwardReferenceProvider? TryGetReverseIndexProvider();
}
