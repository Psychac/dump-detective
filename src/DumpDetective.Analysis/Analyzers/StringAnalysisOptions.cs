namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runtime-injectable options for <see cref="StringAnalyzer"/>.
/// Inject via <c>context.GetOption&lt;StringAnalysisOptions&gt;()</c>.
/// </summary>
public sealed class StringAnalysisOptions
{
    /// <summary>
    /// When the index is available, scalar stats (total count, total bytes, LOH bytes,
    /// Gen2 counts) are derived from TypeAggregates with zero heap scan. Deduplication
    /// still requires a heap scan and is controlled by <see cref="EnableDeduplication"/>
    /// and <see cref="DeduplicationStringCountThreshold"/>.
    /// </summary>

    /// <summary>
    /// Whether to scan the heap to find duplicate string patterns.
    /// Setting to <see langword="false"/> skips the heap scan entirely — stats and sizes
    /// are still reported accurately from the index. Useful when you only need size metrics.
    /// </summary>
    public bool EnableDeduplication { get; init; } = true;

    /// <summary>
    /// Auto-disable deduplication when total string count exceeds this value.
    /// Prevents multi-minute scans on large dumps when dedup wasn't explicitly requested.
    /// Set to <see cref="int.MaxValue"/> to always run dedup regardless of string count.
    /// </summary>
    public int DeduplicationStringCountThreshold { get; init; } = int.MaxValue;

    /// <summary>
    /// Maximum number of unique string fingerprints to track in the dedup map.
    /// Once this limit is reached, new unique string patterns are silently skipped.
    /// Prevents unbounded dictionary growth on dumps with millions of unique strings.
    /// </summary>
    public int MaxUniqueStringTracking { get; init; } = 200_000;

    /// <summary>
    /// Maximum number of strings to read content for during deduplication.
    /// Strings are sampled sequentially from the heap index; content is read via
    /// <c>AsString()</c> which requires random I/O into the dump file.
    /// Default 50,000 balances duplicate coverage vs. I/O cost on large dumps.
    /// Raise to 200,000 for more thorough coverage; lower to 10,000 for fast scans.
    /// Set to <see cref="int.MaxValue"/> to read all candidates (very slow on large dumps).
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
