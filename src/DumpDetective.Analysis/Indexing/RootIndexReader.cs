using System.Buffers.Binary;
using System.Text;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal static class RootIndexReader
{
    private const int RootRecordSize = 20; // TargetAddr(8) | RootAddr(8) | Kind(1) | Pad(3)
    private const int RootHeaderMagic = 0x58495452; // "RTIX"

    // v2 appends a variable-length field-name trailer after the fixed root records; the trailer's
    // record count is stashed in the shared IndexHeader's Reserved field (always 0 in v1). Bumping
    // the version means a v1 cache.bin yields zero roots from disk (not just missing names) until
    // the next full rebuild — accepted trade-off, see docs/analysis/root-field-name-index-plan.md.
    private const int RootHeaderVersion = 2;

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
        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return new List<(ulong, ulong, byte)>();

        return ReadRootIndexFile(reader, cancellationToken);
    }

    /// <summary>
    /// Same as the path-based overload, but reads from a <see cref="CacheContainerReader"/> the
    /// caller already has open — avoids a second, redundant container open when a caller (e.g.
    /// §12.2's <c>ThreadRetentionReaderProvider</c>, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md)
    /// already opened one for another section.
    /// </summary>
    public static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRootIndexFile(CacheContainerReader reader, CancellationToken cancellationToken)
    {
        var roots = new List<(ulong, ulong, byte)>();

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

    /// <summary>
    /// Reads the v2 field-name trailer written by <see cref="Satellite.RootIndexWriter"/>: a
    /// <c>RootAddr → (OwnerType, FieldName, AppDomainId)</c> map for static/thread-static roots,
    /// resolved once at Phase-1 build time. Returns an empty dictionary (not an error) when the
    /// section is missing, the header is a pre-trailer version, or the file is otherwise
    /// unreadable — callers fall back to a live scan in that case
    /// (<see cref="Cache.RootSetCache.GetStaticFieldsByRootAddress"/>).
    /// </summary>
    public static Dictionary<ulong, (string OwnerType, string FieldName, int AppDomainId)> ReadRootFieldNames(
        HeapIndexBuildResult index,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<ulong, (string, string, int)>();

        string containerPath = index.IndexPath;
        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return names;

        if (!reader.TryOpenSection(CacheSectionId.Roots, out Stream? sectionStream) || sectionStream is null)
            return names;

        using Stream stream = sectionStream;

        if (!IndexHeader.TryRead(stream, out IndexHeader header))
            return names;

        if (!header.IsValid(RootHeaderMagic, RootHeaderVersion))
            return names;

        long trailerCount = header.Reserved;
        if (trailerCount <= 0)
            return names;

        // Skip past the fixed root records to reach the trailer.
        stream.Position += header.RecordCount * RootRecordSize;

        Span<byte> prefix = stackalloc byte[16]; // RootAddr(8) | OwnerTypeLen(2) | FieldNameLen(2) | AppDomainId(4)
        byte[] textBuf = new byte[512];

        for (long i = 0; i < trailerCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false) < prefix.Length)
                break;

            ulong rootAddr = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
            int ownerTypeLen = BinaryPrimitives.ReadUInt16LittleEndian(prefix[8..]);
            int fieldNameLen = BinaryPrimitives.ReadUInt16LittleEndian(prefix[10..]);
            int appDomainId = BinaryPrimitives.ReadInt32LittleEndian(prefix[12..]);

            int totalTextLen = ownerTypeLen + fieldNameLen;
            if (totalTextLen > textBuf.Length)
                textBuf = new byte[totalTextLen];

            if (totalTextLen > 0 && stream.ReadAtLeast(textBuf.AsSpan(0, totalTextLen), totalTextLen, throwOnEndOfStream: false) < totalTextLen)
                break;

            string ownerType = Encoding.UTF8.GetString(textBuf, 0, ownerTypeLen);
            string fieldName = Encoding.UTF8.GetString(textBuf, ownerTypeLen, fieldNameLen);
            names[rootAddr] = (ownerType, fieldName, appDomainId);
        }

        return names;
    }

    // Byte values match Microsoft.Diagnostics.Runtime.ClrRootKind (ClrMD 4) exactly;
    // value 6 is unused/skipped in that enum.
    public static string KindToString(byte kind)
        => kind switch
        {
            0 => "None",
            1 => "FinalizerQueue",
            2 => "StrongHandle",
            3 => "PinnedHandle",
            4 => "Stack",
            5 => "RefCountedHandle",
            7 => "AsyncPinnedHandle",
            8 => "SizedRefHandle",
            9 => "ThreadStaticVar",
            10 => "StaticVar",
            _ => $"Unknown({kind})"
        };
}
