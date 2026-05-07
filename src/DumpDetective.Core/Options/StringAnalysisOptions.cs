namespace DumpDetective.Core.Options;

/// <summary>
/// Controls deduplication path selection for the string analyzer.
/// </summary>
public enum DeduplicationMode
{
    Disabled,
    PreferPrebuiltOnly,
    FallbackToHeapScan
}

/// <summary>
/// Sampling intent used to drive numeric caps for heap scanning.
/// </summary>
public enum StringSamplingMode
{
    Aggressive,
    Moderate,
    Full
}

/// <summary>
/// Runtime-injectable options for string analysis behavior.
/// </summary>
public sealed class StringAnalysisOptions
{
    /// <summary>
    /// Whether to scan the heap to find duplicate string patterns.
    /// Setting to <see langword="false"/> skips the heap scan entirely - stats and sizes
    /// are still reported accurately from the index. Useful when you only need size metrics.
    /// </summary>
    public bool EnableDeduplication { get; init; } = true;

    /// <summary>
    /// Auto-disable deduplication when total string count exceeds this value.
    /// Set to <see cref="int.MaxValue"/> to always run dedup regardless of string count.
    /// </summary>
    public int DeduplicationStringCountThreshold { get; init; } = int.MaxValue;

    /// <summary>
    /// Maximum number of unique string fingerprints to track in the dedup map.
    /// Prevents unbounded dictionary growth on dumps with millions of unique strings.
    /// </summary>
    public int MaxUniqueStringTracking { get; init; } = 200_000;

    /// <summary>
    /// Maximum number of strings to read content for during deduplication.
    /// Default 50,000 balances duplicate coverage vs. I/O cost on large dumps.
    /// </summary>
    public int MaxStringsToDedup { get; init; } = 50_000;

    /// <summary>
    /// Number of top duplicate patterns to surface in reports.
    /// </summary>
    public int TopDuplicatesToShow { get; init; } = 20;

    /// <summary>
    /// Threshold in bytes above which a string is considered "very long" for reporting.
    /// Defaults to ~85 KB to approximate LOH boundaries on common runtimes.
    /// </summary>
    public int VeryLongStringThresholdBytes { get; init; } = 85_000;

    /// <summary>
    /// Threshold in bytes for considering a string as LOH-resident for reporting.
    /// Kept separate in case runtimes differ.
    /// </summary>
    public int LohThresholdBytes { get; init; } = 85_000;

    /// <summary>
    /// Maximum preview length (characters) used when rendering string previews in reports.
    /// </summary>
    public int PreviewMaxLength { get; init; } = 80;
    /// <summary>
    /// Maximum duplicate string length (characters) to read when sampling content.
    /// Used to avoid materializing very large strings during deduplication.
    /// </summary>
    public int MaxDuplicateStringLength { get; init; } = 500;

    /// <summary>
    /// Minimum duplicate occurrence count for a string pattern to be considered a duplicate.
    /// </summary>
    public int MinDuplicateStringCount { get; init; } = 10;

    /// <summary>
    /// Controls whether deduplication runs and how it falls back when indexes are missing.
    /// </summary>
    public DeduplicationMode DeduplicationMode { get; init; } = DeduplicationMode.FallbackToHeapScan;

    /// <summary>
    /// Semantic sampling mode hint that can be used to compute caps for heap scanning.
    /// </summary>
    public StringSamplingMode SamplingMode { get; init; } = StringSamplingMode.Moderate;

    /// <summary>
    /// Whether to attempt to detect interned strings by scanning FOH/other intern tables.
    /// </summary>
    public bool DetectInterning { get; init; } = true;

    /// <summary>
    /// When true emit raw CSV/JSON exports of duplicate findings to the report artifacts.
    /// </summary>
    public bool ProduceRawExports { get; init; } = false;

    /// <summary>
    /// Minimum character length for a duplicate to be considered (avoid tiny-noise duplicates).
    /// </summary>
    public int MinDuplicateCharLength { get; init; } = 4;
    public static StringAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new StringAnalysisOptions
        {
            EnableDeduplication = true,
            DeduplicationMode = DeduplicationMode.PreferPrebuiltOnly,
            SamplingMode = StringSamplingMode.Aggressive,
            MaxUniqueStringTracking = 50_000,
            MaxStringsToDedup = 10_000,
            TopDuplicatesToShow = 10,
            PreviewMaxLength = 64,
            MaxDuplicateStringLength = 300,
            MinDuplicateStringCount = 20,
            DetectInterning = false,
            ProduceRawExports = false,
            MinDuplicateCharLength = 8
        },

        AnalysisProfile.Full => new StringAnalysisOptions
        {
            EnableDeduplication = true,
            DeduplicationMode = DeduplicationMode.FallbackToHeapScan,
            SamplingMode = StringSamplingMode.Full,
            MaxUniqueStringTracking = 500_000,
            MaxStringsToDedup = 200_000,
            TopDuplicatesToShow = 50,
            PreviewMaxLength = 120,
            MaxDuplicateStringLength = 2_000,
            MinDuplicateStringCount = 5,
            DetectInterning = true,
            ProduceRawExports = true,
            MinDuplicateCharLength = 1
        },

        _ => new StringAnalysisOptions
        {
            // Balanced / default matches previous defaults
            EnableDeduplication = true,
            DeduplicationMode = DeduplicationMode.FallbackToHeapScan,
            SamplingMode = StringSamplingMode.Moderate,
            MaxUniqueStringTracking = 200_000,
            MaxStringsToDedup = 50_000,
            TopDuplicatesToShow = 20,
            PreviewMaxLength = 80,
            MaxDuplicateStringLength = 500,
            MinDuplicateStringCount = 10,
            DetectInterning = true,
            ProduceRawExports = false,
            MinDuplicateCharLength = 4
        },
    };

    public static StringAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}