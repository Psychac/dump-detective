using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// ArrayAnalyzer domain models

internal sealed record ArrayTypeProfile(
    string ElementTypeName,
    int Rank,
    int Count,
    ulong TotalBytes,
    bool IsMultiDimensional,
    double PercentOfTotalHeapBytes = 0,
    double Gen2PlusLohPercent = 0,
    double AverageInstanceSize = 0,
    string ModuleName = "Unknown",
    // Raw LOH count/bytes and pinned count/bytes for this type+rank, added for Phase 5 step 2
    // (docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook):
    // Gen2PlusLohPercent blends gen2 and LOH into one ratio, so it can't reconstruct
    // ArrayDomainResult.LohArrayBytes/PinnedArrayBytes by summing across types — these can.
    long LohCount = 0,
    ulong LohBytes = 0,
    long PinnedCount = 0,
    ulong PinnedBytes = 0);

internal sealed record LargeArrayEntry(
    ulong Address,
    string ElementTypeName,
    int Length,
    int Rank,
    ulong Size);

internal sealed record SparseArrayEntry(
    ulong Address,
    string ElementTypeName,
    int Length,
    int NullOrZeroCount,
    double SparseRatio,
    ulong WastedBytes);

internal sealed record ArrayDomainResult(
    int TotalArrayObjects,
    ulong TotalArrayBytes,
    int MultiDimArrayCount,
    ulong MultiDimArrayBytes,
    int LohArrayCount,
    ulong LohArrayBytes,
    IReadOnlyList<ArrayTypeProfile> TopArrayTypesBySize,
    IReadOnlyList<LargeArrayEntry> TopLargeArrays,
    IReadOnlyList<SparseArrayEntry> TopSparseArrays,
    int PinnedArrayCount = 0,
    ulong PinnedArrayBytes = 0,
    IReadOnlyList<LargeArrayEntry>? TopPinnedArrays = null) : AnalyzerDomainResult;
