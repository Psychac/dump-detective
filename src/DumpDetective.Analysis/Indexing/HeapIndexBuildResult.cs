namespace DumpDetective.Analysis.Indexing;

// OPT-#14: InMemoryEntries changed from IReadOnlyList<HeapEntry>? to HeapEntry[]?.
// After build the list is never mutated; the array eliminates the List<T> wrapper indirection
// and enables Span<HeapEntry>-based enumeration paths in future without reallocation.
internal sealed record HeapIndexBuildResult(
    HeapIndexStorageKind StorageKind,
    string IndexPath,
    long ObjectCount,
    TimeSpan Elapsed,
    IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> TypeAggregates,
    HeapEntry[]? InMemoryEntries = null);
