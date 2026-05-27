using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// String Analysis

internal sealed record LongStringEntry(ulong Address, int CharLength, ulong SizeBytes);

internal sealed record DistributionSummary(
    IReadOnlyDictionary<string, double> Percentiles,
    IReadOnlyDictionary<string, int> LengthBuckets,
    IReadOnlyDictionary<string, int> FrequencyBuckets,
    int SampleCount);

internal sealed record DuplicateStringSnapshot(
    string Preview,
    int Count,
    ulong WastedBytes,
    IReadOnlyList<ulong>? SampleAddresses = null,
    ulong DominantMethodTable = 0,
    string? DominantType = null,
    ulong? FingerprintHash = null,
    ulong TotalSize = 0,
    int AvgSize = 0,
    string? SamplingSource = null);

internal sealed record StringDomainResult(
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicates,
    IReadOnlyList<LongStringEntry> VeryLongStrings,
    ulong LohStringBytes,
    int InternedStringCount,
    ulong InternedStringBytes,
    int Gen2StringCount,
    ulong Gen2StringBytes,
    bool DeduplicationSkipped,
    int StringsSampled,
    double SamplingCoverage = 0.0,
    // New metadata fields for reporting
    string? SamplingMode = null,
    string? DeduplicationMode = null,
    int DeduplicationThreshold = 0,
    int MaxStringsToDedup = 0,
    string? DedupSource = null,
    long AnalysisDurationMs = 0,
    string? DedupSkipReason = null,
    IReadOnlyList<DumpDetective.Core.Models.NameCountEntry>? TopDuplicateTypes = null,
    DistributionSummary? Distribution = null,
    int PreviewMaxLength = 0,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null) : AnalyzerDomainResult;
