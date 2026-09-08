using DumpDetective.Analysis.Indexing.ReverseIndex;

namespace DumpDetective.Tests.Helpers;

/// <summary>
/// Reads back the raw <c>(child, parent)</c> scratch buckets <see cref="ReverseEdgeExtractor"/>
/// writes. Tests assert against these files rather than an in-process counter because the files are
/// the extractor's only output — Phase B reads nothing else, so anything a counter could report but
/// the file does not contain would be a lie about what was extracted.
/// </summary>
internal static class ReverseEdgeBucketFileReader
{
    private const int EdgeSize = sizeof(ulong) * 2;

    public static string BucketPath(string scratchDir, int bucketIndex) =>
        Path.Combine(scratchDir, $"reverse_edges_bucket_{bucketIndex}{ReverseIndexConstants.TemporaryScratchSuffix}");

    /// <summary>Every edge in one bucket, in write order.</summary>
    public static List<(ulong Child, ulong Parent)> ReadBucket(string scratchDir, int bucketIndex)
    {
        var edges = new List<(ulong Child, ulong Parent)>();
        string path = BucketPath(scratchDir, bucketIndex);
        if (!File.Exists(path))
            return edges;

        using var reader = new BinaryReader(File.OpenRead(path));
        long count = reader.BaseStream.Length / EdgeSize;
        for (long i = 0; i < count; i++)
            edges.Add((reader.ReadUInt64(), reader.ReadUInt64()));

        return edges;
    }

    /// <summary>Every edge across all buckets. Order is per-bucket, so callers that compare sets must sort.</summary>
    public static List<(ulong Child, ulong Parent)> ReadAll(string scratchDir, int bucketCount)
    {
        var edges = new List<(ulong Child, ulong Parent)>();
        for (int i = 0; i < bucketCount; i++)
            edges.AddRange(ReadBucket(scratchDir, i));

        return edges;
    }

    /// <summary>Edge count per bucket, derived from file length so it costs no allocation.</summary>
    public static long[] EdgeCountsPerBucket(string scratchDir, int bucketCount)
    {
        var counts = new long[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            var info = new FileInfo(BucketPath(scratchDir, i));
            counts[i] = info.Exists ? info.Length / EdgeSize : 0;
        }

        return counts;
    }

    public static long TotalEdges(string scratchDir, int bucketCount)
    {
        long total = 0;
        foreach (long count in EdgeCountsPerBucket(scratchDir, bucketCount))
            total += count;

        return total;
    }

    public static int DistinctChildren(string scratchDir, int bucketIndex) =>
        ReadBucket(scratchDir, bucketIndex).Select(e => e.Child).Distinct().Count();
}
