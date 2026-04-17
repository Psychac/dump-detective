using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

internal interface IHeapAnalysisCache
{
    HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap);
    Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap);
    ulong? GetSampleInstanceAddress(string typeName);
    HashSet<ulong> GetRetainedObjects(ClrHeap heap, ulong rootAddress, int maxObjects = 10000);
}
