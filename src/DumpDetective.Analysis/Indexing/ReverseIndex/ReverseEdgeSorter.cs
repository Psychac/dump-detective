using System.Diagnostics;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Phase B: Sorts and indexes raw edge buckets from Phase A.
///
/// For each bucket:
/// 1. Loads raw edges from .tmp file into memory.
/// 2. Sorts by child address.
/// 3. Groups consecutive edges by child, writing sorted groups to .dat file.
/// 4. Builds binary directory index (.idx file) for fast lookup.
///
/// Parallelizable: each bucket can be sorted independently via Task.WhenAll.
/// </summary>
internal class ReverseEdgeSorter
{
    private const long MaxBucketSize = 600 * 1024 * 1024;

    /// <summary>
    /// Sorts all buckets in parallel, returning per-bucket results. Fails fast if any bucket
    /// exceeds MaxBucketSize. Reports one "sorting reverse-index buckets" tick per completed
    /// bucket — buckets can take anywhere from milliseconds to tens of seconds each depending on
    /// fanout skew, so per-bucket-completion is the finest granularity worth surfacing (unlike
    /// Phase A, sorting a single bucket isn't itself broken into observable sub-steps).
    /// </summary>
    public async Task<ReverseIndexSortResult> SortBucketsAsync(
        string cacheDir,
        int bucketCount,
        CancellationToken ct,
        IReadOnlyList<IReadOnlySet<ulong>>? truncatedChildrenPerBucket = null,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        long completed = 0;

        var sortTasks = Enumerable.Range(0, bucketCount)
            .Select(i => SortBucketAsync(cacheDir, i, truncatedChildrenPerBucket?[i], ct, () =>
            {
                long done = Interlocked.Increment(ref completed);
                progress?.Report(new AnalyzerProgressReport(0, "sorting reverse-index buckets",
                    Detail: $"{done}/{bucketCount} buckets", Elapsed: stopwatch.Elapsed));
            }))
            .ToArray();

        var results = await Task.WhenAll(sortTasks);

        return new ReverseIndexSortResult
        {
            BucketDataSizes = results.Select(r => r.DataFileSize).ToList(),
            BucketDirectorySizes = results.Select(r => r.DirectoryFileSize).ToList(),
            BucketElapsedMs = results.Select(r => r.ElapsedMs).ToList(),
            TotalElapsedMs = results.Max(r => r.ElapsedMs),
            PeakMemoryMb = results.Max(r => r.PeakMemoryMb),
        };
    }

    private async Task<BucketSortResult> SortBucketAsync(
        string cacheDir,
        int bucketIdx,
        IReadOnlySet<ulong>? truncatedChildren,
        CancellationToken ct,
        Action onCompleted)
    {
        BucketSortResult result = await Task.Run(() => SortBucketCore(cacheDir, bucketIdx, truncatedChildren), ct);
        onCompleted();
        return result;
    }

    private BucketSortResult SortBucketCore(string cacheDir, int bucketIdx, IReadOnlySet<ulong>? truncatedChildren)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tmpFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}{ReverseIndexConstants.TemporaryScratchSuffix}");
        var dataFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}{ReverseIndexConstants.SortedDataSuffix}");
        var dirFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}{ReverseIndexConstants.DirectorySuffix}");

        // Validate bucket size before loading
        var fileInfo = new FileInfo(tmpFile);
        if (fileInfo.Length > MaxBucketSize)
        {
            throw new InvalidOperationException(
                $"Bucket {bucketIdx} exceeds {MaxBucketSize} bytes ({fileInfo.Length}). " +
                $"Increase bucket count and re-run extraction, or implement external merge-sort.");
        }

        // B1: Load edges from raw file
        var edgeCount = fileInfo.Length / 16;
        var edges = new (ulong child, ulong parent)[edgeCount];

        using (var fs = File.OpenRead(tmpFile))
        using (var reader = new BinaryReader(fs))
        {
            for (long i = 0; i < edgeCount; i++)
            {
                edges[i] = (reader.ReadUInt64(), reader.ReadUInt64());
            }
        }

        // B2: Sort by child address
        Array.Sort(edges, (a, b) => a.child.CompareTo(b.child));

        // B3: Group by child, write data + build directory
        var dirEntries = new List<(ulong childAddr, long fileOffset)>();

        using (var dataWriter = File.Create(dataFile, bufferSize: 65536))
        {
            long currentOffset = 0;

            for (int i = 0; i < edges.Length;)
            {
                var child = edges[i].child;
                var groupStartOffset = currentOffset;
                var parents = new List<ulong>();

                // Collect all parents for this child
                while (i < edges.Length && edges[i].child == child)
                {
                    parents.Add(edges[i].parent);
                    i++;
                }

                // Write group: [child:8][count:4][truncated:1][pad:3][parents:8*count]
                var bw = new BinaryWriter(dataWriter);

                bool truncated = parents.Count > ReverseIndexConstants.MaxParentsPerChild
                    || (truncatedChildren?.Contains(child) ?? false);

                bw.Write(child);
                bw.Write(parents.Count);
                bw.Write(truncated);
                bw.Write(new byte[3]); // padding for alignment

                foreach (var parent in parents)
                    bw.Write(parent);

                dataWriter.Flush();
                currentOffset = dataWriter.Length;

                // Add directory entry
                dirEntries.Add((child, groupStartOffset));
            }
        }

        // B4: Write directory index
        using (var dirWriter = File.Create(dirFile))
        using (var bw = new BinaryWriter(dirWriter))
        {
            // Header (24 bytes)
            bw.Write(ReverseIndexConstants.Magic);
            bw.Write(ReverseIndexConstants.DirectoryVersion);
            bw.Write((long)dirEntries.Count);
            bw.Write(new byte[8]); // reserved

            // Directory entries (16 bytes each)
            foreach (var (childAddr, offset) in dirEntries)
            {
                bw.Write(childAddr);
                bw.Write(offset);
            }
        }

        sw.Stop();

        return new BucketSortResult
        {
            BucketIndex = bucketIdx,
            DataFileSize = new FileInfo(dataFile).Length,
            DirectoryFileSize = new FileInfo(dirFile).Length,
            EdgeCount = edgeCount,
            UniqueChildrenCount = dirEntries.Count,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            PeakMemoryMb = (int)(GC.GetTotalMemory(false) / (1024 * 1024)),
        };
    }
}

/// <summary>
/// Results from sorting all buckets in Phase B.
/// </summary>
internal class ReverseIndexSortResult
{
    public List<long> BucketDataSizes { get; set; } = new();
    public List<long> BucketDirectorySizes { get; set; } = new();
    public List<int> BucketElapsedMs { get; set; } = new();
    public int TotalElapsedMs { get; set; }
    public int PeakMemoryMb { get; set; }
}

/// <summary>
/// Per-bucket result from sorting.
/// </summary>
internal class BucketSortResult
{
    public int BucketIndex { get; set; }
    public long DataFileSize { get; set; }
    public long DirectoryFileSize { get; set; }
    public long EdgeCount { get; set; }
    public long UniqueChildrenCount { get; set; }
    public int ElapsedMs { get; set; }
    public int PeakMemoryMb { get; set; }
}
