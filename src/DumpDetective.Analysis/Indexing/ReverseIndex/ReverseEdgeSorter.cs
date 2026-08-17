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
        var results = new BucketSortResult[bucketCount];

        // Bounded concurrency: each bucket loads up to MaxBucketSize (600MB) of raw edges fully
        // into memory to sort. The previous Task.WhenAll ran every bucket simultaneously with no
        // cap — on a dump with many buckets that's an unbounded multiple of 600MB resident at
        // once. Capping at a small fixed degree of parallelism bounds peak transient memory to a
        // predictable ceiling regardless of bucket count, mirroring
        // DiskBackedObjectIndexWriter's own segment-parallelism cap.
        int maxParallelism = Math.Min(Environment.ProcessorCount, 4);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, bucketCount),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                BucketSortResult result = await Task.Run(
                    () => SortBucketCore(cacheDir, i, bucketCount, truncatedChildrenPerBucket?[i], progress, stopwatch), token);
                results[i] = result;

                long done = Interlocked.Increment(ref completed);
                long totalMb = (result.DataFileSize + result.DirectoryFileSize) / (1024 * 1024);
                progress?.Report(new AnalyzerProgressReport(0, "sorting reverse-index buckets",
                    Detail: $"{done}/{bucketCount} buckets done (bucket {i + 1}: {result.EdgeCount:N0} edges, {totalMb:N0} MB, {result.ElapsedMs / 1000.0:F1}s)",
                    Elapsed: stopwatch.Elapsed));
            });

        return new ReverseIndexSortResult
        {
            BucketDataSizes = results.Select(r => r.DataFileSize).ToList(),
            BucketDirectorySizes = results.Select(r => r.DirectoryFileSize).ToList(),
            BucketElapsedMs = results.Select(r => r.ElapsedMs).ToList(),
            TotalElapsedMs = results.Length == 0 ? 0 : results.Max(r => r.ElapsedMs),
            PeakMemoryMb = results.Length == 0 ? 0 : results.Max(r => r.PeakMemoryMb),
        };
    }

    private BucketSortResult SortBucketCore(
        string cacheDir,
        int bucketIdx,
        int bucketCount,
        IReadOnlySet<ulong>? truncatedChildren,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string bucketLabel = $"bucket {bucketIdx + 1}/{bucketCount}";

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

        // B1: Load edges from raw file into parallel key/value arrays (children/parents) rather
        // than an array of tuples — Array.Sort(keys, values) below sorts via ulong's own
        // IComparable<ulong>, with no per-comparison delegate call, which matters at the hundreds
        // of millions of edges a large dump can produce.
        var edgeCount = fileInfo.Length / 16;
        var children = new ulong[edgeCount];
        var parentAddrs = new ulong[edgeCount];

        using (var fs = File.OpenRead(tmpFile))
        using (var reader = new BinaryReader(fs))
        {
            for (long i = 0; i < edgeCount; i++)
            {
                children[i] = reader.ReadUInt64();
                parentAddrs[i] = reader.ReadUInt64();
            }
        }

        progress?.Report(new AnalyzerProgressReport(0, "sorting reverse-index buckets",
            Detail: $"{bucketLabel}: loaded {edgeCount:N0} edges ({fileInfo.Length / (1024 * 1024):N0} MB), sorting...",
            Elapsed: stopwatch.Elapsed));

        // B2: Sort by child address (keys/values overload — no delegate per comparison)
        Array.Sort(children, parentAddrs);

        progress?.Report(new AnalyzerProgressReport(0, "sorting reverse-index buckets",
            Detail: $"{bucketLabel}: sorted {edgeCount:N0} edges, writing...",
            Elapsed: stopwatch.Elapsed));

        // B3: Group by child, write data + build directory
        var dirEntries = new List<(ulong childAddr, long fileOffset)>();
        long lastWriteProgressReportEdge = 0;

        using (var dataWriter = File.Create(dataFile, bufferSize: 65536))
        {
            // One writer for the whole bucket, offset tracked in memory — the previous version
            // allocated a new BinaryWriter, called Flush() (forces the OS buffer out), and read
            // .Length (a stat syscall) once PER GROUP. With millions of unique children in a large
            // bucket that's millions of syscalls — the actual cause of multi-minute stalls on the
            // write step, not anything progress-reporting could paper over.
            var bw = new BinaryWriter(dataWriter);
            byte[] pad3 = new byte[3];
            long currentOffset = 0;

            for (int i = 0; i < children.Length;)
            {
                var child = children[i];
                var groupStartOffset = currentOffset;

                // The arrays are already sorted by child (B2 above), so every parent for this
                // child is already a contiguous run [i, groupEnd) in `parentAddrs` — no need to
                // copy it into a fresh List<ulong> first (that would allocate one list per unique
                // child; with millions of unique children in a large bucket, that's millions of
                // small heap objects for data that's already sitting right where we need it).
                int groupStart = i;
                while (i < children.Length && children[i] == child)
                    i++;
                int groupCount = i - groupStart;

                // Write group: [child:8][count:4][truncated:1][pad:3][parents:8*count]
                bool truncated = groupCount > ReverseIndexConstants.MaxParentsPerChild
                    || (truncatedChildren?.Contains(child) ?? false);

                bw.Write(child);
                bw.Write(groupCount);
                bw.Write(truncated);
                bw.Write(pad3);

                for (int k = groupStart; k < groupStart + groupCount; k++)
                    bw.Write(parentAddrs[k]);

                currentOffset += 16 + (8L * groupCount); // 8(child) + 4(count) + 1(truncated) + 3(pad) + 8*count(parents)

                // Add directory entry
                dirEntries.Add((child, groupStartOffset));

                if (progress is not null && i - lastWriteProgressReportEdge >= 2_000_000)
                {
                    lastWriteProgressReportEdge = i;
                    progress.Report(new AnalyzerProgressReport(0, "sorting reverse-index buckets",
                        Detail: $"{bucketLabel}: writing... {i:N0}/{edgeCount:N0} edges ({dirEntries.Count:N0} groups)",
                        Elapsed: stopwatch.Elapsed));
                }
            }

            bw.Flush();
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
