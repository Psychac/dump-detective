namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Nine logarithmic size buckets used to build an object-size histogram during Phase 1.
/// Buckets are stored in <see cref="HeapIndexBuildResult.GlobalSizeBuckets"/> (long[9]).
/// </summary>
/// <remarks>
/// Bucket boundaries (inclusive lower, exclusive upper):
///   [0]  &lt;  16 B           tiny value-type boxes, small structs
///   [1]  16–63 B           small objects (most short-lived allocations)
///   [2]  64–255 B          medium-small (typical reference objects)
///   [3]  256–1 023 B       medium
///   [4]  1 024–16 383 B    medium-large (1 KB–16 KB)
///   [5]  16 384–85 000 B   pre-LOH large objects (16 KB–~83 KB)
///   [6]  85 001–1 048 575 B LOH small–medium (85 KB–1 MB)
///   [7]  1 048 576–10 485 759 B LOH medium (1 MB–10 MB)
///   [8]  ≥ 10 485 760 B    LOH very large (≥ 10 MB)
/// The bucket count is written into the on-disk header (see TypeAggregateIndexWriter) and
/// read back dynamically, so this is a purely additive change — old cache.bin files with an
/// 8-bucket histogram still load correctly (consumers gracefully fall back when the loaded
/// array is shorter than <see cref="BucketCount"/>).
/// </remarks>
internal static class SizeBucketHelper
{
    public const int BucketCount = 9;

    // Upper bounds (exclusive). Index 8 has no upper bound (catch-all).
    private static readonly ulong[] s_boundaries =
    [
        16,
        64,
        256,
        1_024,
        16_384,
        85_001,
        1_048_576,
        10_485_760,
    ];

    /// <summary>Labels for each bucket — used in reports and diagnostics.</summary>
    public static readonly string[] BucketLabels =
    [
        "< 16 B",
        "16–63 B",
        "64–255 B",
        "256–1023 B",
        "1 KB–16 KB",
        "16 KB–85 KB",
        "85 KB–1 MB",
        "1 MB–10 MB",
        "≥ 10 MB",
    ];

    /// <summary>
    /// Returns the bucket index (0–7) for the given object size.
    /// Branchless linear scan over 7 thresholds — fast enough for the hot path.
    /// </summary>
    public static int GetBucketIndex(ulong size)
    {
        for (int i = 0; i < s_boundaries.Length; i++)
        {
            if (size < s_boundaries[i])
                return i;
        }
        return BucketCount - 1;
    }
}
