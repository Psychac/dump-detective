using System.Buffers.Binary;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Readers;

internal static class RootIndexReader
{
    private const int RootRecordSize = 20; // TargetAddr(8) | RootAddr(8) | Kind(1) | Pad(3)
    private const int RootHeaderMagic = 0x58495452; // "RTIX"
    private const int RootHeaderVersion = 1;

    public static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRootCandidates(
        HeapIndexBuildResult index,
        CancellationToken cancellationToken)
    {
        if (index.StorageKind == HeapIndexStorageKind.Memory && index.InMemoryRootCandidates is { } candidates)
        {
            var result = new List<(ulong, ulong, byte)>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
                result.Add(candidates[i]);

            return result;
        }

        string indexDir = Path.GetDirectoryName(index.IndexPath) ?? string.Empty;
        string rootPath = Path.Combine(indexDir, DumpIndexPaths.RootIndexFile);
        return ReadRootIndexFile(rootPath, cancellationToken);
    }

    public static List<(string RootKind, ulong Address)> ReadRootTargets(string rootIndexPath, CancellationToken cancellationToken)
    {
        List<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots = ReadRootIndexFile(rootIndexPath, cancellationToken);
        var result = new List<(string RootKind, ulong Address)>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            (ulong targetAddr, _, byte kind) = roots[i];
            result.Add((KindToString(kind), targetAddr));
        }

        return result;
    }

    public static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRootIndexFile(string filePath, CancellationToken cancellationToken)
    {
        var roots = new List<(ulong, ulong, byte)>();
        if (!File.Exists(filePath))
            return roots;

        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

        Span<byte> headerBuf = stackalloc byte[24];
        if (fs.ReadAtLeast(headerBuf, 24, throwOnEndOfStream: false) < 24)
            return roots;

        int magic = BinaryPrimitives.ReadInt32LittleEndian(headerBuf);
        int version = BinaryPrimitives.ReadInt32LittleEndian(headerBuf[4..]);
        if (magic != RootHeaderMagic || version != RootHeaderVersion)
            return roots;

        long recordCount = BinaryPrimitives.ReadInt64LittleEndian(headerBuf[8..]);
        if (recordCount <= 0)
            return roots;

        roots.Capacity = (int)Math.Min(recordCount, 65_536);

        // Read record-by-record via ReadAtLeast: Stream.Read is not guaranteed to
        // return record-aligned byte counts. A batch read into `buf` at offset 0
        // followed by `bytesRead / RootRecordSize` silently drops any trailing
        // partial-record bytes instead of carrying them into the next read — the
        // same bug class fixed in ObjectIndexReader, AsyncTaskAnalyzer, and
        // LohFragmentationAnalyzer. FileStream's own internal buffer (see
        // bufferSize above) makes per-record reads as cheap as batching.
        Span<byte> rec = stackalloc byte[RootRecordSize];
        for (long i = 0; i < recordCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fs.ReadAtLeast(rec, RootRecordSize, throwOnEndOfStream: false) < RootRecordSize)
                break;

            ulong target = BinaryPrimitives.ReadUInt64LittleEndian(rec);
            ulong rootAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
            byte kind = rec[16];
            roots.Add((target, rootAddr, kind));
        }

        return roots;
    }

    public static string KindToString(byte kind)
        => kind switch
        {
            0 => "None",
            1 => "FinalizerQueue",
            2 => "StrongHandle",
            3 => "PinnedHandle",
            4 => "Stack",
            5 => "RefCountedHandle",
            6 => "AsyncPinnedHandle",
            7 => "SizedRefHandle",
            _ => $"Unknown({kind})"
        };
}
