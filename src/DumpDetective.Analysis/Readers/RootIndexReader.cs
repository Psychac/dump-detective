using System.Buffers.Binary;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;

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
        return ReadRootIndexFile(index.IndexPath, cancellationToken);
    }

    public static List<(string RootKind, ulong Address)> ReadRootTargets(string containerPath, CancellationToken cancellationToken)
    {
        List<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots = ReadRootIndexFile(containerPath, cancellationToken);
        var result = new List<(string RootKind, ulong Address)>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            (ulong targetAddr, _, byte kind) = roots[i];
            result.Add((KindToString(kind), targetAddr));
        }

        return result;
    }

    public static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRootIndexFile(string containerPath, CancellationToken cancellationToken)
    {
        var roots = new List<(ulong, ulong, byte)>();

        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return roots;

        if (!reader.TryOpenSection(CacheSectionId.Roots, out Stream? sectionStream) || sectionStream is null)
            return roots;

        using Stream stream = sectionStream;

        if (!IndexHeader.TryRead(stream, out IndexHeader header))
            return roots;

        if (!header.IsValid(RootHeaderMagic, RootHeaderVersion))
            return roots;

        long recordCount = header.RecordCount;
        if (recordCount <= 0)
            return roots;

        roots.Capacity = (int)Math.Min(recordCount, 65_536);

        Span<byte> rec = stackalloc byte[RootRecordSize];
        for (long i = 0; i < recordCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadAtLeast(rec, RootRecordSize, throwOnEndOfStream: false) < RootRecordSize)
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
