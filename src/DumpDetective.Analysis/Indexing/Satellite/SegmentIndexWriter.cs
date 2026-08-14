using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// One GC segment's address range and its record range within the concatenated
/// <see cref="CacheSectionId.ObjectAddresses"/>/<see cref="CacheSectionId.ObjectMethodTables"/>/
/// <see cref="CacheSectionId.ObjectSizes"/> columns.
/// </summary>
internal readonly struct SegmentIndexEntry
{
    public readonly ulong Start;
    public readonly ulong End;
    public readonly long FirstRecordIndex;
    public readonly int RecordCount;

    public SegmentIndexEntry(ulong start, ulong end, long firstRecordIndex, int recordCount)
    {
        Start = start;
        End = end;
        FirstRecordIndex = firstRecordIndex;
        RecordCount = recordCount;
    }
}

/// <summary>
/// Writes/reads the <see cref="CacheSectionId.SegmentIndex"/> satellite section — see
/// docs/cache/cache-architecture.md. Small (segment-count-sized, not object-count-sized),
/// so unlike <c>ObjectAddresses</c>/etc. this is always fully loaded into memory by the reader
/// rather than mmap'd for zero-copy batch access.
/// </summary>
/// <remarks>
/// Record layout (28 bytes, little-endian):
///   Start (8) | End (8) | FirstRecordIndex (8) | RecordCount (4)
/// Segments with zero objects are omitted — a lookup can never land in one.
/// </remarks>
internal static class SegmentIndexWriter
{
    // File magic: "SEGX" (bytes 'S','E','G','X' little-endian) = Segment indeX.
    private const int Magic = 0x58474553;
    private const int Version = 1;
    private const int RecordSize = 28; // 8 + 8 + 8 + 4

    public static void Write(Stream stream, IReadOnlyList<SegmentIndexEntry> entries)
    {
        new IndexHeader(Magic, Version, recordCount: entries.Count).WriteTo(stream);

        Span<byte> rec = stackalloc byte[RecordSize];
        foreach (SegmentIndexEntry entry in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rec, entry.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[8..], entry.End);
            BinaryPrimitives.WriteInt64LittleEndian(rec[16..], entry.FirstRecordIndex);
            BinaryPrimitives.WriteInt32LittleEndian(rec[24..], entry.RecordCount);
            stream.Write(rec);
        }
    }

    /// <summary>
    /// Reads every <see cref="SegmentIndexEntry"/> from <paramref name="containerPath"/>'s
    /// <see cref="CacheSectionId.SegmentIndex"/> section. Returns an empty list — never throws —
    /// when the section is absent, truncated, or from an unrecognized version, matching every other
    /// optional satellite section's "unavailable, not corrupt" contract.
    /// </summary>
    internal static List<SegmentIndexEntry> ReadRecords(string containerPath)
    {
        var result = new List<SegmentIndexEntry>();

        try
        {
            if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.SegmentIndex, out Stream? stream) || stream is null)
                return result;

            using (stream)
            {
                if (!IndexHeader.TryRead(stream, out IndexHeader header) || !header.IsValid(Magic, Version))
                    return result;

                result.Capacity = (int)Math.Min(header.RecordCount, int.MaxValue);

                Span<byte> rec = stackalloc byte[RecordSize];
                for (long i = 0; i < header.RecordCount; i++)
                {
                    int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                    if (read < RecordSize)
                        break;

                    ulong start = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                    ulong end = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                    long firstRecordIndex = BinaryPrimitives.ReadInt64LittleEndian(rec[16..]);
                    int recordCount = BinaryPrimitives.ReadInt32LittleEndian(rec[24..]);
                    result.Add(new SegmentIndexEntry(start, end, firstRecordIndex, recordCount));
                }
            }
        }
        catch (Exception)
        {
            // Section not found or read failed; caller continues without the point-lookup index.
        }

        return result;
    }
}
