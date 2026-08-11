namespace DumpDetective.Analysis.Indexing.ReverseIndex;

internal static class ReverseIndexConstants
{
    public const uint Magic = 0xDEADBEEF;
    public const uint DirectoryVersion = 1;
    public const int MaxParentsPerChild = 10_000;

    /// <summary>
    /// Deterministic Fnv1a 64-bit hash for partitioning child addresses into buckets.
    /// Essential for cache reuse across runs: same child → same bucket always.
    /// </summary>
    public static uint ChildBucketHash(ulong child, int bucketCount)
    {
        unchecked
        {
            const ulong FnvPrime = 0x100000001b3;
            const ulong FnvOffset = 0xcbf29ce484222325;

            ulong hash = FnvOffset ^ child;
            hash = (hash ^ (child >> 32)) * FnvPrime;
            return (uint)(hash % (uint)bucketCount);
        }
    }

    /// <summary>
    /// Calculate bucket count based on dump size to ensure per-bucket memory remains bounded.
    /// Formula: N = max(1, dump_size_gb / 15) keeps per-bucket raw edge data under ~600 MB during sort.
    /// </summary>
    public static int CalculateBucketCount(long dumpSizeBytes)
    {
        var dumpSizeGb = dumpSizeBytes / (1024.0 * 1024 * 1024);
        return Math.Max(1, (int)(dumpSizeGb / 15));
    }

    /// <summary>
    /// Temporary scratch file suffix for raw edge data during Phase A.
    /// </summary>
    public const string TemporaryScratchSuffix = ".tmp";

    /// <summary>
    /// Sorted data file suffix (Phase B output).
    /// </summary>
    public const string SortedDataSuffix = ".dat";

    /// <summary>
    /// Directory index file suffix (Phase B output).
    /// </summary>
    public const string DirectorySuffix = ".idx";
}
