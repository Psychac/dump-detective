using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// String Analysis

/// <summary>
/// Represents a very long string (>85 KB) on the Large Object Heap.
/// </summary>
internal sealed record LongStringEntry(
    ulong Address,
    int CharLength,
    ulong SizeBytes,
    /// <summary>Content preview (truncated to first ~100 chars for readability)</summary>
    string? Preview = null,
    /// <summary>Type name of the object holding this string (if available)</summary>
    string? TypeName = null);

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
    /// <summary>
    /// Count of unique string fingerprints (hash + length + char endpoints) observed in the sample.
    /// This is NOT the count of unique string values in the heap.
    /// When sampling coverage is low (e.g., 1% of 5M strings), this represents the unique patterns
    /// in that 1% sample only. Derived from: <c>dedup_skipped ? 0 : stringStats.Count</c>.
    /// Used in <see cref="DuplicationRatio"/> calculation; consumers should gate interpretation on <see cref="SamplingCoverage"/>.
    /// </summary>
    int SampledUniquePatterns,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    /// <summary>
    /// Ratio of duplicate occurrence count to total strings: <c>(TotalStrings - SampledUniquePatterns) / TotalStrings</c>.
    /// Interpretation is only meaningful when <see cref="SamplingCoverage"/> is high (≥ 0.05 / 5%).
    /// At low coverage, the ratio is an artifact of the sample size and does not reflect heap-wide distribution.
    /// </summary>
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicates,
    IReadOnlyList<LongStringEntry> VeryLongStrings,
    ulong LohStringBytes,
    int InternedStringCount,
    ulong InternedStringBytes,
    long Gen2StringCount,
    ulong Gen2StringBytes,
    int StringsSampled,
    double SamplingCoverage = 0.0,
    string? DedupSource = null,
    long AnalysisDurationMs = 0,
    IReadOnlyList<DumpDetective.Core.Models.NameCountEntry>? TopDuplicateTypes = null,
    /// <summary>Top object types by total string bytes they reference (not duplicate strings, all strings)</summary>
    IReadOnlyList<(string TypeName, ulong TotalBytes)>? TopStringOwnerTypes = null,
    DistributionSummary? Distribution = null,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null) : AnalyzerDomainResult;
