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
    Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> GetStaticFieldsByTargetAddress(ClrHeap heap);
    Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap);
    ulong? GetSampleInstanceAddress(string typeName);
    IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap);
    int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount);
    bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable);
    IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples();

    /// <summary>
    /// Resolves <paramref name="address"/>'s <c>(MethodTable, Size)</c> via the disk-backed
    /// <c>SegmentIndex</c> point lookup when available (see docs/cache/19-ObjectAddressLookupIndex.md),
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
}
