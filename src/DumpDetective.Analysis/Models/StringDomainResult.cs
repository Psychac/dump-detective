using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// String Analysis

internal sealed record LongStringEntry(ulong Address, int CharLength, ulong SizeBytes);

internal sealed record StringDomainResult(
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByWaste,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByCount,
    IReadOnlyList<LongStringEntry> VeryLongStrings,
    ulong LohStringBytes,
    int InternedStringCount,
    ulong InternedStringBytes,
    int Gen2StringCount,
    ulong Gen2StringBytes,
    bool DeduplicationSkipped,
    int StringsSampled,
    double SamplingCoverage = 0.0,
    IReadOnlyList<DumpDetective.Core.Models.NameCountEntry>? TopDuplicateTypes = null,
    int PreviewMaxLength = 0) : AnalyzerDomainResult;
