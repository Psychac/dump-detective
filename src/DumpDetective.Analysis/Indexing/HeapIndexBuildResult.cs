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
    HeapEntry[]? InMemoryEntries = null,
    IReadOnlyList<ModuleInfo>? Modules = null,
    /// <summary>
    /// 8-element heap-wide object-size histogram built during Phase 1.
    /// Bucket boundaries are defined in <see cref="SizeBucketHelper.BucketLabels"/>.
    /// Always 64 bytes — never null after a successful build.
    /// </summary>
    long[]? GlobalSizeBuckets = null,
    /// <summary>
    /// Per-MethodTable field layout cache built during Phase 1.
    /// ~800 KB for 50 K types. Used by ObjectShapeAnalyzer, BoxingAnalyzer, and DominatorAnalyzer.
    /// </summary>
    IReadOnlyDictionary<ulong, TypeShapeEntry>? TypeShapeCache = null,
    /// <summary>
    /// Pre-filtered list of Task/ValueTask addresses collected during Phase 1 (memory-backed mode only).
    /// Mirrors the subset that <c>TaskIndex.bin</c> contains in disk-backed mode, allowing
    /// <see cref="Analyzers.AsyncTaskAnalyzer"/> to skip a full O(N) scan of <see cref="InMemoryEntries"/>.
    /// </summary>
    (ulong Addr, ulong Mt)[]? InMemoryTaskCandidates = null);
