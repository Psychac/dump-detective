namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// JSON payload of the <c>ForwardEdgeMetadata</c> cache.bin section — mirrors
/// <see cref="ReverseIndex.ReverseIndexMetadata"/>, keyed by parent instead of child, with no
/// fanout-cap fields (§ForwardIndexConstants).
/// </summary>
internal sealed class ForwardIndexMetadata
{
    public int BucketCount { get; set; }
    public string HashFunction { get; set; } = "Fnv1a64";
    public long TotalEdgesRecorded { get; set; }
    public List<ForwardIndexBucketLocation> Buckets { get; set; } = new();
}

/// <summary>
/// Byte range of a single bucket's data and directory payload within the
/// <c>ForwardEdgeBuckets</c> / <c>ForwardEdgeDirectories</c> sections respectively. Offsets are
/// relative to the start of each section, not the start of cache.bin.
/// </summary>
internal sealed class ForwardIndexBucketLocation
{
    public int BucketIndex { get; set; }
    public long DataOffset { get; set; }
    public long DataLength { get; set; }
    public long DirectoryOffset { get; set; }
    public long DirectoryLength { get; set; }
}
