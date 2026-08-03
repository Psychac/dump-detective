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
    double AverageInstanceSize = 0);

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
    bool ScanLimited) : AnalyzerDomainResult;
