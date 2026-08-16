using System.Diagnostics;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Phase B: sorts and indexes raw edge buckets from <see cref="ForwardEdgeExtractor"/>, grouping
/// by parent (source) address — mirrors <see cref="ReverseIndex.ReverseEdgeSorter"/>. Group record
/// layout is simpler (no truncated flag/padding, since this index is never capped):
/// [parent:8][count:4][children:8*count].
/// </summary>
internal sealed class ForwardEdgeSorter
{
    private const long MaxBucketSize = 600 * 1024 * 1024;

    public async Task<ForwardIndexSortResult> SortBucketsAsync(
        string cacheDir,
        int bucketCount,
        CancellationToken ct,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        long completed = 0;

        var sortTasks = Enumerable.Range(0, bucketCount)
            .Select(i => SortBucketAsync(cacheDir, i, bucketCount, ct, progress, stopwatch, result =>
            {
                long done = Interlocked.Increment(ref completed);
                long totalMb = (result.DataFileSize + result.DirectoryFileSize) / (1024 * 1024);
                progress?.Report(new AnalyzerProgressReport(0, "sorting forward-index buckets",
                    Detail: $"{done}/{bucketCount} buckets done (bucket {i + 1}: {result.EdgeCount:N0} edges, {totalMb:N0} MB, {result.ElapsedMs / 1000.0:F1}s)",
                    Elapsed: stopwatch.Elapsed));
            }))
            .ToArray();

        var results = await Task.WhenAll(sortTasks);

        return new ForwardIndexSortResult
        {
            BucketDataSizes = results.Select(r => r.DataFileSize).ToList(),
            BucketDirectorySizes = results.Select(r => r.DirectoryFileSize).ToList(),
            TotalElapsedMs = results.Length == 0 ? 0 : results.Max(r => r.ElapsedMs),
            PeakMemoryMb = results.Length == 0 ? 0 : results.Max(r => r.PeakMemoryMb),
        };
    }

    private async Task<BucketSortResult> SortBucketAsync(
        string cacheDir,
        int bucketIdx,
        int bucketCount,
        CancellationToken ct,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch,
        Action<BucketSortResult> onCompleted)
    {
        BucketSortResult result = await Task.Run(
            () => SortBucketCore(cacheDir, bucketIdx, bucketCount, progress, stopwatch), ct);
        onCompleted(result);
        return result;
    }

    private BucketSortResult SortBucketCore(
        string cacheDir,
        int bucketIdx,
        int bucketCount,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        var sw = Stopwatch.StartNew();
        string bucketLabel = $"bucket {bucketIdx + 1}/{bucketCount}";

        var tmpFile = Path.Combine(cacheDir, $"forward_edges_bucket_{bucketIdx}{ForwardIndexConstants.TemporaryScratchSuffix}");
        var dataFile = Path.Combine(cacheDir, $"forward_edges_bucket_{bucketIdx}{ForwardIndexConstants.SortedDataSuffix}");
        var dirFile = Path.Combine(cacheDir, $"forward_edges_bucket_{bucketIdx}{ForwardIndexConstants.DirectorySuffix}");

        var fileInfo = new FileInfo(tmpFile);
        if (fileInfo.Length > MaxBucketSize)
        {
            throw new InvalidOperationException(
                $"Forward-index bucket {bucketIdx} exceeds {MaxBucketSize} bytes ({fileInfo.Length}). " +
                $"Increase bucket count and re-run extraction.");
        }

        var edgeCount = fileInfo.Length / 16;
        var parents = new ulong[edgeCount];
        var children = new ulong[edgeCount];

        using (var fs = File.OpenRead(tmpFile))
        using (var reader = new BinaryReader(fs))
        {
            for (long i = 0; i < edgeCount; i++)
            {
                parents[i] = reader.ReadUInt64();
                children[i] = reader.ReadUInt64();
            }
        }

        progress?.Report(new AnalyzerProgressReport(0, "sorting forward-index buckets",
            Detail: $"{bucketLabel}: loaded {edgeCount:N0} edges ({fileInfo.Length / (1024 * 1024):N0} MB), sorting...",
            Elapsed: stopwatch.Elapsed));

        Array.Sort(parents, children);

        progress?.Report(new AnalyzerProgressReport(0, "sorting forward-index buckets",
            Detail: $"{bucketLabel}: sorted {edgeCount:N0} edges, writing...",
            Elapsed: stopwatch.Elapsed));

        var dirEntries = new List<(ulong ParentAddr, long FileOffset)>();
        long lastWriteProgressReportEdge = 0;

        using (var dataWriter = File.Create(dataFile, bufferSize: 65536))
        {
            var bw = new BinaryWriter(dataWriter);
            long currentOffset = 0;

            for (int i = 0; i < parents.Length;)
            {
                ulong parent = parents[i];
                long groupStartOffset = currentOffset;
                var groupChildren = new List<ulong>();

                while (i < parents.Length && parents[i] == parent)
                {
                    groupChildren.Add(children[i]);
                    i++;
                }

                // [parent:8][count:4][children:8*count] — no truncated flag/padding, this index
                // is never capped (§ForwardIndexConstants).
                bw.Write(parent);
                bw.Write(groupChildren.Count);
                foreach (ulong child in groupChildren)
                    bw.Write(child);

                currentOffset += 12 + (8L * groupChildren.Count);
                dirEntries.Add((parent, groupStartOffset));

                if (progress is not null && i - lastWriteProgressReportEdge >= 2_000_000)
                {
                    lastWriteProgressReportEdge = i;
                    progress.Report(new AnalyzerProgressReport(0, "sorting forward-index buckets",
                        Detail: $"{bucketLabel}: writing... {i:N0}/{edgeCount:N0} edges ({dirEntries.Count:N0} groups)",
                        Elapsed: stopwatch.Elapsed));
                }
            }

            bw.Flush();
        }

        using (var dirWriter = File.Create(dirFile))
        using (var bw = new BinaryWriter(dirWriter))
        {
            bw.Write(ForwardIndexConstants.Magic);
            bw.Write(ForwardIndexConstants.DirectoryVersion);
            bw.Write((long)dirEntries.Count);
            bw.Write(new byte[8]); // reserved

            foreach ((ulong parentAddr, long offset) in dirEntries)
            {
                bw.Write(parentAddr);
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
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            PeakMemoryMb = (int)(GC.GetTotalMemory(false) / (1024 * 1024)),
        };
    }
}

internal sealed class ForwardIndexSortResult
{
    public List<long> BucketDataSizes { get; set; } = new();
    public List<long> BucketDirectorySizes { get; set; } = new();
    public int TotalElapsedMs { get; set; }
    public int PeakMemoryMb { get; set; }
}

internal sealed class BucketSortResult
{
    public int BucketIndex { get; set; }
    public long DataFileSize { get; set; }
    public long DirectoryFileSize { get; set; }
    public long EdgeCount { get; set; }
    public int ElapsedMs { get; set; }
    public int PeakMemoryMb { get; set; }
}
