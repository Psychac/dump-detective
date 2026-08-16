namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Mirrors <see cref="ReverseIndex.ReverseIndexConstants"/>, keyed by parent (source) address
/// instead of child — see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D5. No fanout cap: out-degree
/// (how many fields/array elements one object has) has no hub-fanout problem the way in-degree
/// does (§D3) — never in the millions the way a hub object's incoming-reference count can be.
/// </summary>
internal static class ForwardIndexConstants
{
    public const uint Magic = 0xF0DBEEF0;
    public const uint DirectoryVersion = 1;

    /// <summary>
    /// Deterministic Fnv1a 64-bit hash partitioning parent addresses into buckets. Same function
    /// as <see cref="ReverseIndex.ReverseIndexConstants.ChildBucketHash"/>, applied to the parent
    /// (source) address instead of the child — essential for cache reuse across runs.
    /// </summary>
    public static uint ParentBucketHash(ulong parent, int bucketCount)
    {
        unchecked
        {
            const ulong FnvPrime = 0x100000001b3;
            const ulong FnvOffset = 0xcbf29ce484222325;

            ulong hash = FnvOffset ^ parent;
            hash = (hash ^ (parent >> 32)) * FnvPrime;
            return (uint)(hash % (uint)bucketCount);
        }
    }

    /// <summary>
    /// Same sizing formula as <see cref="ReverseIndex.ReverseIndexConstants.CalculateBucketCount"/>
    /// — dump-size-based, not edge-count-based, so it applies equally well here despite the very
    /// different per-bucket edge-count profile (forward edges are ~2.35 per object on average,
    /// measured, vs. the reverse index's hub-skewed distribution).
    /// </summary>
    public static int CalculateBucketCount(long dumpSizeBytes)
    {
        var dumpSizeMb = dumpSizeBytes / (1024.0 * 1024);
        return Math.Max(1, (int)Math.Ceiling(dumpSizeMb / 500));
    }

    public const string TemporaryScratchSuffix = ".tmp";
    public const string SortedDataSuffix = ".dat";
    public const string DirectorySuffix = ".idx";
}
