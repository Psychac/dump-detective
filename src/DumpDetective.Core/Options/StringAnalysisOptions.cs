namespace DumpDetective.Core.Options;

/// <summary>
/// Runtime-injectable options for string analysis behavior.
/// </summary>
public sealed class StringAnalysisOptions
{
    /// <summary>
    /// Maximum number of unique string fingerprints to track in the dedup map.
    /// Genuine resident-bytes cap: prevents unbounded dictionary growth on dumps with
    /// millions of unique strings. This is the only thing standing between the current
    /// design and unbounded growth once every string is fingerprinted.
    /// </summary>
    public int MaxUniqueStringTracking { get; init; } = 200_000;

    /// <summary>
    /// Maximum duplicate string length (characters) to read when sampling content.
    /// Materialization guard: without this, a single 100 MB string becomes a 200 MB
    /// managed allocation during dedup. Bounds <c>ClrObject.AsString(maxLength:)</c>.
    /// </summary>
    public int MaxDuplicateStringLength { get; init; } = 500;

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
    /// Minimum duplicate occurrence count for a string pattern to be considered a duplicate.
    /// </summary>
    public int MinDuplicateStringCount { get; init; } = 10;

    /// <summary>
    /// Minimum character length for a duplicate to be considered (avoid tiny-noise duplicates).
    /// </summary>
    public int MinDuplicateCharLength { get; init; } = 4;

    /// <summary>
    /// When true emit raw CSV/JSON/NDJSON exports of duplicate findings to the report artifacts.
    /// </summary>
    public bool ProduceRawExports { get; init; } = false;

    /// <summary>
    /// Number of top duplicate patterns (by wasted bytes) to run a GC root-path search for (P3-2,
    /// string-analyzer-audit.md). Each search is a bounded but real traversal — this caps the
    /// number of searches performed, not the amount of duplicate data reported. Set to 0 to
    /// disable retention-path sampling entirely.
    /// </summary>
    public int RetentionPathSampleCount { get; init; } = 5;
}
