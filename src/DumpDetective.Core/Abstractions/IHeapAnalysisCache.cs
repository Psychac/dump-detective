using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public interface IHeapAnalysisCache
{
    long ObjectScanCount { get; }
    long CacheHits { get; }
    long CacheMisses { get; }

    HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap);
    Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap);
    ulong? GetSampleInstanceAddress(string typeName);
    HashSet<ulong> GetRetainedObjects(ClrHeap heap, ulong rootAddress, int maxObjects = 10000);
    IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap);
}
