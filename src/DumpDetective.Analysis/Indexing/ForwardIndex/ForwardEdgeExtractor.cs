using System.Diagnostics;
using System.Text;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Phase A: extracts and partitions heap edges into hash-partitioned buckets, keyed by parent
/// (source) address — mirrors <see cref="ReverseIndex.ReverseEdgeExtractor"/>, minus the
/// fanout-cap/truncation bookkeeping that index needs and this one doesn't (§ForwardIndexConstants:
/// out-degree has no hub-fanout problem). Simpler as a result: no per-child count tracking, no
/// truncated-set tracking, just a per-bucket edge counter for statistics.
///
/// Scratch files are written to &lt;cacheDir&gt;/forward_edges_bucket_&lt;i&gt;.tmp
/// Raw edge format: [ParentAddress: ulong(8)] [ChildAddress: ulong(8)] per record.
/// </summary>
internal sealed class ForwardEdgeExtractor : IAsyncDisposable
{
    private readonly int _bucketCount;
    private readonly BinaryWriter[] _bucketWriters;
    private readonly long[] _edgeCountPerBucket;
    private readonly object[] _bucketLocks;

    public ForwardEdgeExtractor(int bucketCount, string cacheDir)
    {
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be positive.");

        _bucketCount = bucketCount;
        _bucketWriters = new BinaryWriter[bucketCount];
        _edgeCountPerBucket = new long[bucketCount];
        _bucketLocks = new object[bucketCount];

        for (int i = 0; i < bucketCount; i++)
        {
            _bucketLocks[i] = new object();

            var path = Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.TemporaryScratchSuffix}");
            var fs = File.Create(path, bufferSize: 65536);
            _bucketWriters[i] = new BinaryWriter(fs, Encoding.Default, leaveOpen: false);
        }
    }

    /// <summary>
    /// Records a single (parent, child) edge, routing it to the appropriate bucket.
    /// Thread-safe: multiple threads can call this concurrently.
    /// </summary>
    public void RecordEdge(ulong parent, ulong child)
    {
        int bucketIdx = (int)ForwardIndexConstants.ParentBucketHash(parent, _bucketCount);

        lock (_bucketLocks[bucketIdx])
        {
            _bucketWriters[bucketIdx].Write(parent);
            _bucketWriters[bucketIdx].Write(child);
            _edgeCountPerBucket[bucketIdx]++;
        }
    }

    /// <summary>
    /// Records a batch of (parent, child) edges already known to belong to
    /// <paramref name="bucketIdx"/>, taking the bucket's lock once instead of once per edge —
    /// mirrors <see cref="ReverseIndex.ReverseEdgeExtractor.RecordEdgesBatch"/>. Callers accumulate
    /// edges per-bucket locally (e.g. per <c>Parallel.For</c> worker) and flush in batches.
    /// Clears <paramref name="edges"/> after flushing so callers can reuse the same list instance.
    /// </summary>
    public void RecordEdgesBatch(int bucketIdx, List<(ulong Parent, ulong Child)> edges)
    {
        if (edges.Count == 0)
            return;

        lock (_bucketLocks[bucketIdx])
        {
            BinaryWriter writer = _bucketWriters[bucketIdx];
            foreach ((ulong parent, ulong child) in edges)
            {
                writer.Write(parent);
                writer.Write(child);
            }
            _edgeCountPerBucket[bucketIdx] += edges.Count;
        }

        edges.Clear();
    }

    /// <summary>Returns statistics collected during extraction: edge count per bucket.</summary>
    public ForwardEdgeExtractionStats GetStatistics()
    {
        long totalEdges = 0;
        var bucketStats = new List<ForwardEdgeBucketStats>(_bucketCount);

        for (int i = 0; i < _bucketCount; i++)
        {
            long edgeCount;
            lock (_bucketLocks[i])
                edgeCount = _edgeCountPerBucket[i];

            totalEdges += edgeCount;
            bucketStats.Add(new ForwardEdgeBucketStats { BucketIndex = i, EdgeCount = edgeCount });
        }

        return new ForwardEdgeExtractionStats
        {
            BucketCount = _bucketCount,
            TotalEdgesRecorded = totalEdges,
            BucketStats = bucketStats,
        };
    }

    /// <summary>Flushes all bucket writers and disposes resources.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(progress: null);

    /// <summary>
    /// Flushes and closes each bucket's writer. Mirrors
    /// <see cref="ReverseIndex.ReverseEdgeExtractor.DisposeAsync(IProgress{AnalyzerProgressReport}?)"/>'s
    /// per-bucket progress reporting for large dumps.
    /// </summary>
    public async ValueTask DisposeAsync(IProgress<AnalyzerProgressReport>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        int flushed = 0;

        for (int i = 0; i < _bucketCount; i++)
        {
            BinaryWriter? writer = _bucketWriters[i];
            if (writer is null)
                continue;

            try
            {
                await Task.Run(() =>
                {
                    lock (_bucketLocks[i])
                    {
                        writer.Flush();
                        writer.Dispose();
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — best-effort cleanup.
            }

            flushed++;
            progress?.Report(new AnalyzerProgressReport(0, "flushing forward-index edges",
                Detail: $"{flushed}/{_bucketCount} buckets", Elapsed: stopwatch.Elapsed));
        }
    }
}

/// <summary>Statistics from Phase A edge extraction.</summary>
internal sealed class ForwardEdgeExtractionStats
{
    public int BucketCount { get; set; }
    public long TotalEdgesRecorded { get; set; }
    public List<ForwardEdgeBucketStats> BucketStats { get; set; } = new();
}

/// <summary>Per-bucket statistics from Phase A extraction.</summary>
internal sealed class ForwardEdgeBucketStats
{
    public int BucketIndex { get; set; }
    public long EdgeCount { get; set; }
}
