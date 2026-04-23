namespace DumpDetective.Analysis.Indexing;

internal readonly record struct TypeAggregateIndexEntry(
    ulong MethodTable,
    long Count,
    ulong TotalSize,
    long LohCount,
    ulong LohSize,
    ulong SampleAddress);
