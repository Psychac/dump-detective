namespace DumpDetective.Core.Options;

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
}