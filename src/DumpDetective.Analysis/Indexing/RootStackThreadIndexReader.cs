using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads the <c>RootStackThreadAttribution</c> section written by
/// <see cref="Satellite.RootStackThreadIndexWriter"/> — §12.2
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Loaded fully into memory
/// at read time, same "bounded by root count, not object count" precedent
/// <see cref="RootIndexReader.ReadRootIndexFile(CacheContainerReader, CancellationToken)"/> already
/// sets for the sibling <c>Roots</c> section.
/// </summary>
internal static class RootStackThreadIndexReader
{
    private const int RecordSize = 16; // RootAddr(8) | OSThreadId(4) | ManagedThreadId(4)
    private const int HeaderMagic = 0x41545452; // "RTTA"
    private const int HeaderVersion = 1;

    /// <summary>
    /// Returns an empty dictionary (not an error) when the section is missing — a legacy
    /// pre-§12.2 cache.bin, or <c>SkipRootIndexBuild</c> was set at build time — matching every
    /// other optional-satellite-index contract in this codebase.
    /// </summary>
    public static Dictionary<ulong, (uint OSThreadId, int ManagedThreadId)> Read(
        CacheContainerReader reader, CancellationToken cancellationToken)
    {
        var map = new Dictionary<ulong, (uint OSThreadId, int ManagedThreadId)>();

        if (!reader.TryOpenSection(CacheSectionId.RootStackThreadAttribution, out Stream? sectionStream) || sectionStream is null)
            return map;

        using Stream stream = sectionStream;

        if (!IndexHeader.TryRead(stream, out IndexHeader header))
            return map;

        if (!header.IsValid(HeaderMagic, HeaderVersion))
            return map;

        long recordCount = header.RecordCount;
        if (recordCount <= 0)
            return map;

        map.EnsureCapacity((int)Math.Min(recordCount, 65_536));

        Span<byte> rec = stackalloc byte[RecordSize];
        for (long i = 0; i < recordCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false) < RecordSize)
                break;

            ulong rootAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
            uint osThreadId = BinaryPrimitives.ReadUInt32LittleEndian(rec[8..]);
            int managedThreadId = BinaryPrimitives.ReadInt32LittleEndian(rec[12..]);
            map[rootAddr] = (osThreadId, managedThreadId);
        }

        return map;
    }
}
