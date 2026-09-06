namespace DumpDetective.Analysis.Indexing.ReverseIndex;

internal static class ReverseIndexConstants
{
    /// <summary>
    /// Deterministic Fnv1a 64-bit hash for partitioning child addresses into buckets.
    /// Essential for cache reuse across runs: same child → same bucket always. Still load-bearing
    /// under true CSR (format v8, docs/cache/cache-format-clean-slate-redesign.md §2) — it's what
    /// guarantees every edge sharing a child lands in exactly one bucket, which is what lets Phase B
    /// count and fill the CSR across buckets in parallel with no locking (see
    /// <see cref="ReverseEdgeCsrBuilder"/>'s remarks).
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
    /// Formula: N = ceil(dump_size_mb / 500) keeps per-bucket raw edge data under ~500 MB during sort.
    /// Validated against 3.27 GB and 25.63 GB dumps with perfect distribution uniformity (3.7-3.9% CV).
    /// Reference: pre-implementation-validation.md Investigation 2.
    /// </summary>
    public static int CalculateBucketCount(long dumpSizeBytes)
    {
        var dumpSizeMb = dumpSizeBytes / (1024.0 * 1024);
        return Math.Max(1, (int)Math.Ceiling(dumpSizeMb / 500));
    }

    /// <summary>
    /// Phase A's raw <c>(child, parent)</c> address-pair scratch file suffix — the only scratch file
    /// this index still produces since format v8 removed the per-bucket sorted <c>.dat</c>/<c>.idx</c>
    /// intermediates (docs/cache/cache-format-clean-slate-redesign.md §2.2): Phase B now resolves
    /// these files directly into the CSR arrays in memory, with nothing written back to disk until
    /// the finished container.
    /// </summary>
    public const string TemporaryScratchSuffix = ".tmp";
}
