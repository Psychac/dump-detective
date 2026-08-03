namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Per-MethodTable aggregate statistics built during Phase 1 heap scan.
/// Stored in <see cref="HeapIndexBuildResult.TypeAggregates"/>.
/// </summary>
/// <remarks>
/// Binary record layout (TypeAggregateIndex.bin, 80 bytes):
///   MT(8) | ModuleId(4) | Count(8) | TotalSize(8) | LohCount(8) | LohSize(8) |
///   SampleAddress(8) | Gen0Count(8) | Gen1Count(8) | Gen2Count(8) | Flags(1) | Pad(3)
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
    TypeAggregateFlags Flags = TypeAggregateFlags.None);
