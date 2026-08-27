namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Per-MethodTable aggregate statistics built during Phase 1 heap scan.
/// Stored in <see cref="HeapIndexBuildResult.TypeAggregates"/>.
/// </summary>
/// <remarks>
/// Binary record layout (TypeAggregateIndex.bin, 88 bytes):
///   MT(8) | ModuleId(4) | Count(8) | TotalSize(8) | LohCount(8) | LohSize(8) |
///   SampleAddress(8) | Gen0Count(8) | Gen1Count(8) | Gen2Count(8) | Gen2TotalSize(8) |
///   Flags(1) | Pad(3)
/// </remarks>
internal readonly record struct TypeAggregateIndexEntry(
    ulong MethodTable,
    int ModuleId,
    long Count,
    ulong TotalSize,
    long LohCount,
    ulong LohSize,
    ulong SampleAddress,
    long Gen0Count = 0,
    long Gen1Count = 0,
    long Gen2Count = 0,
    TypeAggregateFlags Flags = TypeAggregateFlags.None,
    /// <summary>
    /// Exact sum of object sizes for this type's non-LOH Gen2 instances only (accumulated
    /// during the Phase 1 scan — see <c>TypeIndexBuilder.Add</c>). Unlike deriving a Gen2 byte
    /// estimate from <c>Gen2Count * (TotalSize / Count)</c> — which assumes every instance of
    /// the type is the same size — this is exact even for types whose Gen2 and non-Gen2
    /// instances differ substantially in size (e.g. growing collections).
    /// </summary>
    ulong Gen2TotalSize = 0);
