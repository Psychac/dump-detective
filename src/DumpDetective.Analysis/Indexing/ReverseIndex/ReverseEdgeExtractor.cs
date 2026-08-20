namespace DumpDetective.Analysis.Indexing.ReverseIndex;

using System.Diagnostics;
using System.Text;

using DumpDetective.Core.Abstractions;

/// <summary>
/// Phase A: Extracts and partitions heap edges into hash-partitioned buckets.
///
/// During heap streaming, records (parent, child) edges by hashing the child address
/// to determine bucket assignment. Uncapped by design — every edge is written, however large a
/// single child's fan-in gets (see docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md
/// §4.2/§7.4: real dumps measured up to a 10.7M-parent hub without the per-bucket sort ever
/// approaching its memory ceiling, so no cap or hub-overflow routing is needed).
///
/// Scratch files are written to &lt;cacheDir&gt;/reverse_edges_bucket_<i>.tmp
/// Raw edge format: [ChildAddress: ulong(8)] [ParentAddress: ulong(8)] per record.
/// </summary>
internal class ReverseEdgeExtractor : IAsyncDisposable
{
    private readonly int _bucketCount;
    private readonly BinaryWriter[] _bucketWriters;

    /// <summary>
    /// Per-bucket fanout counter: exact, uncapped incoming-edge count for each child within that
    /// bucket — used for reporting (§5's `DominatorReachableInDegree`-style diagnostics), not to
    /// enforce any cap.
    /// </summary>
    private readonly Dictionary<ulong, int>[] _fanoutPerBucket;

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
        _bucketLocks = new object[bucketCount];

        for (int i = 0; i < bucketCount; i++)
        {
            _bucketLocks[i] = new object();

            var path = Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.TemporaryScratchSuffix}");
            var fs = File.Create(path, bufferSize: 65536);
            _bucketWriters[i] = new BinaryWriter(fs, Encoding.Default, leaveOpen: false);
            _fanoutPerBucket[i] = new Dictionary<ulong, int>(capacity: 1024);
        }
    }

    /// <summary>
    /// Records a single (parent, child) edge, routing it to the appropriate bucket. Uncapped —
    /// every edge is written, regardless of how large the child's fan-in gets.
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

            fanout[child] = count + 1;
            _bucketWriters[bucketIdx].Write(child);
            _bucketWriters[bucketIdx].Write(parent);
        }
    }

    /// <summary>
    /// Records a batch of (child, parent) edges already known to belong to
    /// <paramref name="bucketIdx"/>, taking that bucket's lock once instead of once per edge.
    /// Callers accumulate edges per-bucket locally (e.g. per <c>Parallel.For</c> worker) and flush
    /// in batches — at hundreds of millions of edges the fixed per-call cost of <c>lock</c> is the
    /// dominant overhead of <see cref="RecordEdge"/>, not the dictionary/write work itself.
    /// Clears <paramref name="edges"/> after flushing so callers can reuse the same list instance.
    /// </summary>
    public void RecordEdgesBatch(int bucketIdx, List<(ulong Child, ulong Parent)> edges)
    {
        if (edges.Count == 0)
            return;

        lock (_bucketLocks[bucketIdx])
        {
            Dictionary<ulong, int> fanout = _fanoutPerBucket[bucketIdx];
            BinaryWriter writer = _bucketWriters[bucketIdx];

            foreach ((ulong child, ulong parent) in edges)
            {
                if (!fanout.TryGetValue(child, out int count))
                    count = 0;

                fanout[child] = count + 1;
                writer.Write(child);
                writer.Write(parent);
            }
        }

        edges.Clear();
    }

    /// <summary>
    /// Returns statistics collected during extraction: exact, uncapped edge count per bucket.
    /// </summary>
    public ReverseEdgeExtractionStats GetStatistics()
    {
        long totalEdges = 0;
        var bucketStats = new List<ReverseEdgeBucketStats>();

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
                });
            }
        }

        return new ReverseEdgeExtractionStats
        {
            BucketCount = _bucketCount,
            TotalEdgesRecorded = totalEdges,
            BucketStats = bucketStats,
        };
    }

    /// <summary>
    /// Flushes all bucket writers and disposes resources.
    /// </summary>
    public ValueTask DisposeAsync() => DisposeAsync(progress: null);

    /// <summary>
    /// Flushes and closes every bucket's writer. On a large dump this can take a genuinely long
    /// time (one <see cref="FileStream"/> flush per bucket, hundreds of buckets, gigabytes of raw
    /// edges) — reports one "flushing reverse-index edges" tick per bucket so it doesn't look
    /// stalled next to the sort phase's per-bucket reporting.
    /// </summary>
    public async ValueTask DisposeAsync(IProgress<AnalyzerProgressReport>? progress)
    {
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < _bucketWriters.Length; i++)
        {
            BinaryWriter? writer = _bucketWriters[i];
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

            progress?.Report(new AnalyzerProgressReport(0, "flushing reverse-index edges",
                Detail: $"{i + 1}/{_bucketCount} buckets", Elapsed: stopwatch.Elapsed));
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
}
