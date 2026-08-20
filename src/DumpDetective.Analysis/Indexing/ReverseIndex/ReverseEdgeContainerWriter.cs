using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Phase C: merges the per-bucket <c>.dat</c>/<c>.idx</c> files produced by
/// <see cref="ReverseEdgeSorter"/> into the <c>ReverseEdgeBuckets</c>, <c>ReverseEdgeDirectories</c>
/// and <c>ReverseEdgeMetadata</c> sections of an open <see cref="CacheContainerWriter"/>, then
/// deletes the scratch files. All three sections are written together — a failure aborts whichever
/// section is currently open, leaving the container without any reverse-index sections rather than
/// a partial/inconsistent one.
/// </summary>
internal static class ReverseEdgeContainerWriter
{
    private const int CopyBufferSize = 64 * 1024;
    private const long CopyProgressThresholdBytes = 16L * 1024 * 1024;
    private const long CopyProgressReportEveryBytes = 32L * 1024 * 1024;

    public static void Write(
        CacheContainerWriter containerWriter,
        string cacheDir,
        int bucketCount,
        ReverseEdgeExtractionStats extractionStats,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var dataLocations = new List<(long Offset, long Length)>(bucketCount);
        var dirLocations = new List<(long Offset, long Length)>(bucketCount);

        try
        {
            // Hash each section's bytes as they're copied instead of via EndSection's default
            // re-read pass — these merges are pure data concatenation with no placeholder-header-
            // patched-afterward step, and on large dumps this section can run into the GBs, where a
            // full re-read is real added wall-clock.
            var bucketsHasher = new XxHash32();
            containerWriter.BeginSection(CacheSectionId.ReverseEdgeBuckets);
            long sectionStart = containerWriter.Stream.Position;
            for (int i = 0; i < bucketCount; i++)
            {
                string dataFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.SortedDataSuffix}");
                long offset = containerWriter.Stream.Position - sectionStart;
                long length = AppendFile(containerWriter.Stream, dataFile, bucketsHasher, progress, stopwatch,
                    label: $"bucket {i + 1}/{bucketCount} (data)");
                dataLocations.Add((offset, length));
                progress?.Report(new AnalyzerProgressReport(0, "merging reverse-index into cache.bin",
                    Detail: $"{i + 1}/{bucketCount} buckets (data, {length / (1024 * 1024):N0} MB)", Elapsed: stopwatch.Elapsed));
            }
            containerWriter.EndSection(bucketCount, bucketsHasher.GetCurrentHashAsUInt32());

            var directoriesHasher = new XxHash32();
            containerWriter.BeginSection(CacheSectionId.ReverseEdgeDirectories);
            sectionStart = containerWriter.Stream.Position;
            for (int i = 0; i < bucketCount; i++)
            {
                string dirFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.DirectorySuffix}");
                long offset = containerWriter.Stream.Position - sectionStart;
                long length = AppendFile(containerWriter.Stream, dirFile, directoriesHasher, progress, stopwatch,
                    label: $"bucket {i + 1}/{bucketCount} (directory)");
                dirLocations.Add((offset, length));
                progress?.Report(new AnalyzerProgressReport(0, "merging reverse-index into cache.bin",
                    Detail: $"{i + 1}/{bucketCount} buckets (directory, {length / (1024 * 1024):N0} MB)", Elapsed: stopwatch.Elapsed));
            }
            containerWriter.EndSection(bucketCount, directoriesHasher.GetCurrentHashAsUInt32());

            var metadata = new ReverseIndexMetadata
            {
                BucketCount = bucketCount,
                // Uncapped since §4.2/§7.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) —
                // int.MaxValue signals "no cap" to any reader still checking this field for
                // on-disk format compatibility; nothing is ever truncated anymore.
                MaxParentsPerChild = int.MaxValue,
                TotalEdgesRecorded = extractionStats.TotalEdgesRecorded,
                TotalTruncatedChildren = 0,
            };
            for (int i = 0; i < bucketCount; i++)
            {
                metadata.Buckets.Add(new ReverseIndexBucketLocation
                {
                    BucketIndex = i,
                    DataOffset = dataLocations[i].Offset,
                    DataLength = dataLocations[i].Length,
                    DirectoryOffset = dirLocations[i].Offset,
                    DirectoryLength = dirLocations[i].Length,
                });
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
            containerWriter.BeginSection(CacheSectionId.ReverseEdgeMetadata);
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
    /// length. A missing file (e.g. an empty bucket never created by the sorter) contributes a
    /// zero-length slice. Reports periodically for files large enough that a single silent copy
    /// could otherwise look like a stall (see <see cref="CopyProgressThresholdBytes"/>).
    /// </summary>
    private static long AppendFile(Stream stream, string path, XxHash32 hasher, IProgress<AnalyzerProgressReport>? progress, Stopwatch stopwatch, string label)
    {
        if (!File.Exists(path))
            return 0;

        long fileLength = new FileInfo(path).Length;
        bool reportProgress = progress is not null && fileLength >= CopyProgressThresholdBytes;
        long processedSinceLastReport = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: CopyBufferSize, FileOptions.SequentialScan);
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                stream.Write(buffer, 0, read);
                hasher.Append(buffer.AsSpan(0, read));
                total += read;

                if (reportProgress)
                {
                    processedSinceLastReport += read;
                    if (processedSinceLastReport >= CopyProgressReportEveryBytes)
                    {
                        processedSinceLastReport = 0;
                        progress!.Report(new AnalyzerProgressReport(0, "merging reverse-index into cache.bin",
                            Detail: $"{label}: {total / (1024 * 1024):N0}/{fileLength / (1024 * 1024):N0} MB copied",
                            Elapsed: stopwatch.Elapsed));
                    }
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
            TryDelete(Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.TemporaryScratchSuffix}"));
            TryDelete(Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.SortedDataSuffix}"));
            TryDelete(Path.Combine(cacheDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.DirectorySuffix}"));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
