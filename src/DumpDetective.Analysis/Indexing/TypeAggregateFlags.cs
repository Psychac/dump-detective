namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Per-type classification bits stored in <see cref="TypeAggregateIndexEntry.Flags"/>.
/// Written during Phase 1 heap scan; consumed by downstream analyzers to filter the
/// TypeAggregates dictionary without re-scanning the heap.
/// </summary>
/// <remarks>
/// Bit layout (matches TypeAggregateIndex.bin record — 1 byte):
///   bit 0 = IsStringType      → StringAnalyzer
///   bit 1 = IsTaskType        → AsyncTaskAnalyzer
///   bit 2 = IsDelegateType    → EventLeakAnalyzer
///   bit 3 = IsFinalizableType → FinalizableObjectAnalyzer
///   bit 4 = IsArrayType       → ArrayAnalyzer
///   bits 5–7 = reserved
/// </remarks>
[Flags]
internal enum TypeAggregateFlags : byte
{
    None = 0,
    IsStringType = 1 << 0,
    IsTaskType = 1 << 1,
    IsDelegateType = 1 << 2,
    IsFinalizableType = 1 << 3,
    IsArrayType = 1 << 4,
}
