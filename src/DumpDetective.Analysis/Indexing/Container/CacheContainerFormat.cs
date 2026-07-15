using System.Buffers.Binary;
using System.Text;

namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Stable identifiers for each section stored in <c>cache.bin</c>. Values are persisted in the
/// TOC and must never be renumbered — appending new members is safe, reordering is not.
/// </summary>
internal enum CacheSectionId
{
    /// <summary>Unused since format version 2 — superseded by the columnar Object* sections below.</summary>
    Objects = 0,
    TypeAggregates = 1,
    Roots = 2,
    Handles = 3,
    Tasks = 4,
    EventCandidates = 5,
    LargeObjects = 6,
    LohFreeBlocks = 7,
    StringDedup = 8,
    StringDedupMeta = 9,
    /// <summary>Columnar <c>ulong[]</c> of object addresses, one per heap object.</summary>
    ObjectAddresses = 10,
    /// <summary>Columnar <c>ulong[]</c> of object method tables, aligned with <see cref="ObjectAddresses"/>.</summary>
    ObjectMethodTables = 11,
    /// <summary>Columnar <c>ulong[]</c> of object sizes, aligned with <see cref="ObjectAddresses"/>.</summary>
    ObjectSizes = 12,
}

/// <summary>
/// Fixed 64-byte header at offset 0 of <c>cache.bin</c>.
/// </summary>
/// <remarks>
/// Layout (little-endian):
///   Offset  0 — Magic (8 bytes)            ASCII "DDCACHE1"
///   Offset  8 — FormatVersion (4 bytes)     int, readers reject unsupported versions
///   Offset 12 — DumpContentHash (32 bytes)  reserved, zero-filled (content-addressed key is a later item)
///   Offset 44 — SectionCount (4 bytes)      int
///   Offset 48 — TocOffset (8 bytes)         long, always equal to <see cref="Size"/>
///   Offset 56 — Reserved (8 bytes)          zero
/// Total = 64 bytes
/// </remarks>
internal readonly struct CacheFileHeader
{
    public const int Size = 64;
    /// <summary>
    /// Bumped to 2 when the Objects section moved from an interleaved array-of-structs
    /// layout to columnar ObjectAddresses/ObjectMethodTables/ObjectSizes sections — old
    /// cache.bin files fail <see cref="TryRead"/> and are rebuilt rather than misparsed.
    /// </summary>
    public const int CurrentFormatVersion = 2;

    private const int MagicOffset = 0;
    private const int MagicSize = 8;
    private const int FormatVersionOffset = MagicOffset + MagicSize;
    private const int DumpContentHashOffset = FormatVersionOffset + 4;
    private const int DumpContentHashSize = 32;
    private const int SectionCountOffset = DumpContentHashOffset + DumpContentHashSize;
    private const int TocOffsetOffset = SectionCountOffset + 4;

    private static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("DDCACHE1");

    public readonly int FormatVersion;
    public readonly int SectionCount;
    public readonly long TocOffset;

    public CacheFileHeader(int sectionCount, long tocOffset)
    {
        FormatVersion = CurrentFormatVersion;
        SectionCount = sectionCount;
        TocOffset = tocOffset;
    }

    /// <summary>Writes this header into the first <see cref="Size"/> bytes of <paramref name="stream"/>.</summary>
    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        ExpectedMagic.CopyTo(buf);
        BinaryPrimitives.WriteInt32LittleEndian(buf[FormatVersionOffset..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(buf[SectionCountOffset..], SectionCount);
        BinaryPrimitives.WriteInt64LittleEndian(buf[TocOffsetOffset..], TocOffset);
        stream.Write(buf);
    }

    /// <summary>
    /// Reads the header from the current stream position (must be at offset 0).
    /// Returns <c>false</c> if the stream is too short, the magic doesn't match, or the
    /// format version is unsupported.
    /// </summary>
    public static bool TryRead(Stream stream, out CacheFileHeader header)
    {
        Span<byte> buf = stackalloc byte[Size];
        int read = stream.ReadAtLeast(buf, Size, throwOnEndOfStream: false);
        if (read < Size || !buf.Slice(MagicOffset, MagicSize).SequenceEqual(ExpectedMagic))
        {
            header = default;
            return false;
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(buf[FormatVersionOffset..]);
        if (version != CurrentFormatVersion)
        {
            header = default;
            return false;
        }

        header = new CacheFileHeader(
            BinaryPrimitives.ReadInt32LittleEndian(buf[SectionCountOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[TocOffsetOffset..]));
        return true;
    }
}

/// <summary>
/// One 32-byte table-of-contents entry describing a single section's location in <c>cache.bin</c>.
/// </summary>
/// <remarks>
/// Layout (little-endian):
///   Offset  0 — SectionId (4 bytes)    <see cref="CacheSectionId"/>
///   Offset  4 — Offset (8 bytes)       absolute byte offset into cache.bin
///   Offset 12 — Length (8 bytes)       section byte length
///   Offset 20 — RecordCount (8 bytes)  number of records in the section
///   Offset 28 — Checksum (4 bytes)     XxHash32 of the section's bytes; written but not yet
///                                      validated on read (corruption-resilience is a later item)
/// Total = 32 bytes
/// </remarks>
internal readonly struct CacheTocEntry
{
    public const int Size = 32;

    public readonly CacheSectionId SectionId;
    public readonly long Offset;
    public readonly long Length;
    public readonly long RecordCount;
    public readonly uint Checksum;

    public CacheTocEntry(CacheSectionId sectionId, long offset, long length, long recordCount, uint checksum)
    {
        SectionId = sectionId;
        Offset = offset;
        Length = length;
        RecordCount = recordCount;
        Checksum = checksum;
    }

    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, (int)SectionId);
        BinaryPrimitives.WriteInt64LittleEndian(buf[4..], Offset);
        BinaryPrimitives.WriteInt64LittleEndian(buf[12..], Length);
        BinaryPrimitives.WriteInt64LittleEndian(buf[20..], RecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[28..], Checksum);
        stream.Write(buf);
    }

    public static CacheTocEntry ReadFrom(ReadOnlySpan<byte> buf) =>
        new(
            (CacheSectionId)BinaryPrimitives.ReadInt32LittleEndian(buf),
            BinaryPrimitives.ReadInt64LittleEndian(buf[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buf[28..]));
}
