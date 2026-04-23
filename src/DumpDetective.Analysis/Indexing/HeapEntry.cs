namespace DumpDetective.Analysis.Indexing;

internal readonly record struct HeapEntry(ulong Address, ulong MethodTable, ulong Size);
