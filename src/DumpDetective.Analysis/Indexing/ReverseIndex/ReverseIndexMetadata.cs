namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// JSON payload of the <c>ReverseEdgeMetadata</c> cache.bin section. Since the container's TOC
/// has one fixed slot per <see cref="Container.CacheSectionId"/> rather than one per bucket, all
/// buckets' data/directory payloads are concatenated into the two <c>ReverseEdgeBuckets</c> /
/// <c>ReverseEdgeDirectories</c> sections back to back; this metadata records where each bucket's
/// slice starts and how long it is within those two sections.
/// </summary>
internal sealed class ReverseIndexMetadata
{
    public int BucketCount { get; set; }
    public int MaxParentsPerChild { get; set; }
    public string HashFunction { get; set; } = "Fnv1a64";
    public long TotalEdgesRecorded { get; set; }
    public int TotalTruncatedChildren { get; set; }
    public List<ReverseIndexBucketLocation> Buckets { get; set; } = new();
}

/// <summary>
/// Byte range of a single bucket's data and directory payload within the
/// <c>ReverseEdgeBuckets</c> / <c>ReverseEdgeDirectories</c> sections respectively.
/// Offsets are relative to the start of each section, not the start of cache.bin.
/// </summary>
internal sealed class ReverseIndexBucketLocation
{
    public int BucketIndex { get; set; }
    public long DataOffset { get; set; }
    public long DataLength { get; set; }
    public long DirectoryOffset { get; set; }
    public long DirectoryLength { get; set; }
}
