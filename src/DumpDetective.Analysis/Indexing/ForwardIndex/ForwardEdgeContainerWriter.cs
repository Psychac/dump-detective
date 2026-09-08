using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Phase C: merges per-bucket <c>.dat</c>/<c>.idx</c> files produced by
/// <see cref="ForwardEdgeSorter"/> into <c>ForwardEdgeBuckets</c>, <c>ForwardEdgeDirectories</c>
/// and <c>ForwardEdgeMetadata</c> sections of an open <see cref="CacheContainerWriter"/>, then
/// deletes scratch files. Mirrors <see cref="ReverseIndex.ReverseEdgeContainerWriter"/>.
/// </summary>
internal static class ForwardEdgeContainerWriter
{
    private const int CopyBufferSize = 64 * 1024;
    private const long CopyProgressThresholdBytes = 16L * 1024 * 1024;
    private const long CopyProgressReportEveryBytes = 32L * 1024 * 1024;

    public static void Write(
        CacheContainerWriter containerWriter,
        string cacheDir,
        int bucketCount,
        ForwardEdgeExtractionStats extractionStats,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var dataLocations = new List<(long Offset, long Length)>(bucketCount);
        var dirLocations = new List<(long Offset, long Length)>(bucketCount);

        try
        {
            var bucketsHasher = new XxHash32();
            containerWriter.BeginSection(CacheSectionId.ForwardEdgeBuckets);
            long dataSectionStart = containerWriter.Stream.Position;
            for (int i = 0; i < bucketCount; i++)
            {
                var dataFile = Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.SortedDataSuffix}");
                long offset = containerWriter.Stream.Position - dataSectionStart;
                long length = AppendFile(containerWriter.Stream, dataFile, bucketsHasher, progress, stopwatch,
                    label: $"bucket {i + 1}/{bucketCount} (data)");
                dataLocations.Add((offset, length));
                progress?.Report(new AnalyzerProgressReport(0, "merging forward-index into cache.bin",
                    Detail: $"{i + 1}/{bucketCount} buckets ({length / (1024 * 1024):N0} MB)", Elapsed: stopwatch.Elapsed));
            }
            containerWriter.EndSection(bucketCount, bucketsHasher.GetCurrentHashAsUInt32());

            var directoriesHasher = new XxHash32();
            containerWriter.BeginSection(CacheSectionId.ForwardEdgeDirectories);
            long dirSectionStart = containerWriter.Stream.Position;
            for (int i = 0; i < bucketCount; i++)
            {
                var dirFile = Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.DirectorySuffix}");
                long offset = containerWriter.Stream.Position - dirSectionStart;
                long length = AppendFile(containerWriter.Stream, dirFile, directoriesHasher, progress, stopwatch,
                    label: $"bucket {i + 1}/{bucketCount} (directory)");
                dirLocations.Add((offset, length));
                progress?.Report(new AnalyzerProgressReport(0, "merging forward-index into cache.bin",
                    Detail: $"{i + 1}/{bucketCount} buckets (directory, {length / (1024 * 1024):N0} MB)", Elapsed: stopwatch.Elapsed));
            }
            containerWriter.EndSection(bucketCount, directoriesHasher.GetCurrentHashAsUInt32());

            var metadata = new ForwardIndexMetadata
            {
                BucketCount = bucketCount,
                TotalEdgesRecorded = extractionStats.TotalEdgesRecorded,
            };
            for (int i = 0; i < bucketCount; i++)
            {
                metadata.Buckets.Add(new ForwardIndexBucketLocation
                {
                    BucketIndex = i,
                    DataOffset = dataLocations[i].Offset,
                    DataLength = dataLocations[i].Length,
                    DirectoryOffset = dirLocations[i].Offset,
                    DirectoryLength = dirLocations[i].Length,
                });
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
            containerWriter.BeginSection(CacheSectionId.ForwardEdgeMetadata);
            containerWriter.Stream.Write(json, 0, json.Length);
            containerWriter.EndSection(1);
        }
        catch
        {
            try { containerWriter.AbortSection(); } catch { /* no section was open */ }
            throw;
        }

        DeleteScratchFiles(cacheDir, bucketCount);
    }

    /// <summary>
    /// Streams <paramref name="path"/> onto the end of <paramref name="stream"/>, returning its
    /// length. A missing file (e.g. an empty bucket) contributes zero length.
    /// </summary>
    private static long AppendFile(Stream stream, string path, XxHash32 hasher,
        IProgress<AnalyzerProgressReport>? progress, Stopwatch stopwatch, string label)
    {
        if (!File.Exists(path))
            return 0;

        long fileLength = new FileInfo(path).Length;
        bool reportProgress = progress is not null && fileLength >= CopyProgressThresholdBytes;
        long total = 0;
        long lastReported = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.SequentialScan);

            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                stream.Write(buffer, 0, read);
                hasher.Append(buffer.AsSpan(0, read));
                total += read;

                if (reportProgress && total - lastReported >= CopyProgressReportEveryBytes)
                {
                    lastReported = total;
                    progress!.Report(new AnalyzerProgressReport(0, "merging forward-index into cache.bin",
                        Detail: $"{label}: {total / (1024 * 1024):N0}/{fileLength / (1024 * 1024):N0} MB copied",
                        Elapsed: stopwatch.Elapsed));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private static void DeleteScratchFiles(string cacheDir, int bucketCount)
    {
        for (int i = 0; i < bucketCount; i++)
        {
            TryDelete(Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.TemporaryScratchSuffix}"));
            TryDelete(Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.SortedDataSuffix}"));
            TryDelete(Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.DirectorySuffix}"));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
