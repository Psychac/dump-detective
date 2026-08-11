namespace DumpDetective.Analysis.Indexing.ReverseIndex;

using System.Text;

/// <summary>
/// Phase A: Extracts and partitions heap edges into hash-partitioned buckets.
///
/// During heap streaming, records (parent, child) edges by hashing the child address
/// to determine bucket assignment. Implements fanout capping (max 10K parents per child)
/// and tracks truncated children for later reporting.
///
/// Scratch files are written to &lt;cacheDir&gt;/reverse_edges_bucket_<i>.tmp
/// Raw edge format: [ChildAddress: ulong(8)] [ParentAddress: ulong(8)] per record.
/// </summary>
internal class ReverseEdgeExtractor : IAsyncDisposable
{
    private readonly int _bucketCount;
    private readonly BinaryWriter[] _bucketWriters;

    /// <summary>
    /// Per-bucket fanout counter: tracks edge count for each child within that bucket.
    /// Used to enforce MaxParentsPerChild cap.
    /// </summary>
    private readonly Dictionary<ulong, int>[] _fanoutPerBucket;

    /// <summary>
    /// Per-bucket set of children that were truncated (hit MaxParentsPerChild cap).
    /// Reported in metadata for diagnostics.
    /// </summary>
    private readonly HashSet<ulong>[] _truncatedPerBucket;

    /// <summary>
    /// Lock protecting concurrent writes to bucket files. Each bucket has its own lock
    /// to minimize contention during streaming.
    /// </summary>
    private readonly object[] _bucketLocks;

    /// <summary>
    /// Creates a new edge extractor with N buckets, creating scratch files in <paramref name="cacheDir"/>.
    /// </summary>
    public ReverseEdgeExtractor(int bucketCount, string cacheDir)
    {
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be positive.");

        _bucketCount = bucketCount;
        _bucketWriters = new BinaryWriter[bucketCount];
        _fanoutPerBucket = new Dictionary<ulong, int>[bucketCount];
        _truncatedPerBucket = new HashSet<ulong>[bucketCount];
        _bucketLocks = new object[bucketCount];

        for (int i = 0; i < bucketCount; i++)
        {
            _bucketLocks[i] = new object();

            var path = Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.TemporaryScratchSuffix}");
            var fs = File.Create(path, bufferSize: 65536);
            _bucketWriters[i] = new BinaryWriter(fs, Encoding.Default, leaveOpen: false);
            _fanoutPerBucket[i] = new Dictionary<ulong, int>(capacity: 1024);
            _truncatedPerBucket[i] = new HashSet<ulong>();
        }
    }

    /// <summary>
    /// Records a single (parent, child) edge, routing it to the appropriate bucket.
    /// If child would exceed MaxParentsPerChild, edge is dropped and child marked truncated.
    /// Thread-safe: multiple threads can call this concurrently.
    /// </summary>
    public void RecordEdge(ulong parent, ulong child)
    {
        int bucketIdx = (int)ReverseIndexConstants.ChildBucketHash(child, _bucketCount);

        lock (_bucketLocks[bucketIdx])
        {
            var fanout = _fanoutPerBucket[bucketIdx];

            if (!fanout.TryGetValue(child, out int count))
                count = 0;

            if (count >= ReverseIndexConstants.MaxParentsPerChild)
            {
                _truncatedPerBucket[bucketIdx].Add(child);
                return;
            }

            fanout[child] = count + 1;
            _bucketWriters[bucketIdx].Write(child);
            _bucketWriters[bucketIdx].Write(parent);
        }
    }

    /// <summary>
    /// Returns statistics collected during extraction: edge count per bucket and truncation info.
    /// </summary>
    public ReverseEdgeExtractionStats GetStatistics()
    {
        long totalEdges = 0;
        var bucketStats = new List<ReverseEdgeBucketStats>();
        int totalTruncatedChildren = 0;

        for (int i = 0; i < _bucketCount; i++)
        {
            long bucketEdges = 0;

            lock (_bucketLocks[i])
            {
                bucketEdges = _fanoutPerBucket[i].Values.Sum(count => (long)count);
                totalEdges += bucketEdges;

                bucketStats.Add(new ReverseEdgeBucketStats
                {
                    BucketIndex = i,
                    EdgeCount = bucketEdges,
                    UniqueChildrenCount = _fanoutPerBucket[i].Count,
                    TruncatedChildrenCount = _truncatedPerBucket[i].Count,
                });

                totalTruncatedChildren += _truncatedPerBucket[i].Count;
            }
        }

        return new ReverseEdgeExtractionStats
        {
            BucketCount = _bucketCount,
            TotalEdgesRecorded = totalEdges,
            TotalTruncatedChildren = totalTruncatedChildren,
            BucketStats = bucketStats,
        };
    }

    /// <summary>
    /// Flushes all bucket writers and disposes resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var writer in _bucketWriters)
        {
            if (writer != null)
            {
                try
                {
                    writer.Flush();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, skip
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// Statistics from Phase A edge extraction.
/// </summary>
internal class ReverseEdgeExtractionStats
{
    public int BucketCount { get; set; }
    public long TotalEdgesRecorded { get; set; }
    public int TotalTruncatedChildren { get; set; }
    public List<ReverseEdgeBucketStats> BucketStats { get; set; } = new();
}

/// <summary>
/// Per-bucket statistics from Phase A extraction.
/// </summary>
internal class ReverseEdgeBucketStats
{
    public int BucketIndex { get; set; }
    public long EdgeCount { get; set; }
    public long UniqueChildrenCount { get; set; }
    public int TruncatedChildrenCount { get; set; }
}
