namespace DumpDetective.Analysis.Indexing;

internal readonly record struct TypeAggregateIndexEntry(
    ulong MethodTable,
    int ModuleId,
    long Count,
    ulong TotalSize,
    long LohCount,
    ulong LohSize,
    ulong SampleAddress);
