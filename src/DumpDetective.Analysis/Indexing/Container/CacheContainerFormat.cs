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
    /// <summary>Columnar <c>sbyte[]</c> of per-object GC generations, aligned with <see cref="ObjectAddresses"/>.</summary>
    ObjectGenerations = 13,
    /// <summary>
    /// Concatenated sorted-group payloads (<c>.dat</c>) from every reverse-edge bucket, back to
    /// back in bucket order. Per-bucket byte ranges are recorded in <see cref="ReverseEdgeMetadata"/>
    /// since the container's TOC has one fixed slot per <see cref="CacheSectionId"/>, not one per bucket.
    /// </summary>
    ReverseEdgeBuckets = 14,
    /// <summary>
    /// Concatenated directory-index payloads (<c>.idx</c>) from every reverse-edge bucket, back to
    /// back in bucket order, mirroring <see cref="ReverseEdgeBuckets"/>.
    /// </summary>
    ReverseEdgeDirectories = 15,
    /// <summary>JSON <see cref="Indexing.ReverseIndex.ReverseIndexMetadata"/>: bucket count and per-bucket offsets/lengths into the two sections above, plus extraction stats.</summary>
    ReverseEdgeMetadata = 16,
    /// <summary>
    /// Small per-segment table of (Start, End, FirstRecordIndex, RecordCount) — see
    /// <see cref="Indexing.Satellite.SegmentIndexWriter"/> and
    /// docs/cache/cache-architecture.md. Enables <c>ObjectAddressLookup</c>'s
    /// binary-search point lookup (address → MethodTable/Size) without a container FormatVersion
    /// bump: a missing section here just means the disk-backed point lookup is unavailable and
    /// callers fall back to <c>heap.GetObject</c>, the same "absent section" contract every other
    /// optional satellite section already has.
    /// </summary>
    SegmentIndex = 17,
}

/// <summary>
/// Fixed 64-byte header at offset 0 of <c>cache.bin</c>.
/// </summary>
/// <remarks>
/// Layout (little-endian):
///   Offset  0 — Magic (8 bytes)            ASCII "DDCACHE1"
///   Offset  8 — FormatVersion (4 bytes)     int, readers reject unsupported versions
///   Offset 12 — DumpContentHash (32 bytes)  <see cref="DumpContentHasher"/> signature; zero-filled if unknown
///   Offset 44 — SectionCount (4 bytes)      int
///   Offset 48 — TocOffset (8 bytes)         long, always equal to <see cref="Size"/>
///   Offset 56 — Reserved (8 bytes)          zero
/// Total = 64 bytes
/// </remarks>
internal readonly struct CacheFileHeader
{
    public const int Size = 64;
    /// <summary>
    /// Bumped to 4 when the ReverseEdgeBuckets/ReverseEdgeDirectories/ReverseEdgeMetadata
    /// sections were added for the disk-backed reverse-reference index — old cache.bin files
    /// fail <see cref="TryRead"/> and are rebuilt rather than misparsed.
    /// Previously bumped to 3 when the columnar ObjectGenerations section (per-object GC
    /// generation, 1 byte/sbyte) was added alongside ObjectAddresses/ObjectMethodTables/ObjectSizes.
    /// Previously bumped to 2 when the Objects section moved from an interleaved
    /// array-of-structs layout to those columnar sections.
    /// </summary>
    public const int CurrentFormatVersion = 4;

    private const int MagicOffset = 0;
    private const int MagicSize = 8;
    private const int FormatVersionOffset = MagicOffset + MagicSize;
    private const int DumpContentHashOffset = FormatVersionOffset + 4;
    private const int DumpContentHashSize = 32;
    private const int SectionCountOffset = DumpContentHashOffset + DumpContentHashSize;
    private const int TocOffsetOffset = SectionCountOffset + 4;

    private static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("DDCACHE1");

    public readonly int FormatVersion;
    public readonly byte[] DumpContentHash;
    public readonly int SectionCount;
    public readonly long TocOffset;

    public CacheFileHeader(int sectionCount, long tocOffset, byte[]? dumpContentHash = null)
    {
        FormatVersion = CurrentFormatVersion;
        DumpContentHash = dumpContentHash ?? new byte[DumpContentHashSize];
        SectionCount = sectionCount;
        TocOffset = tocOffset;
    }

    /// <summary>Writes this header into the first <see cref="Size"/> bytes of <paramref name="stream"/>.</summary>
    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        ExpectedMagic.CopyTo(buf);
        BinaryPrimitives.WriteInt32LittleEndian(buf[FormatVersionOffset..], FormatVersion);
        DumpContentHash.AsSpan().CopyTo(buf.Slice(DumpContentHashOffset, DumpContentHashSize));
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
            BinaryPrimitives.ReadInt64LittleEndian(buf[TocOffsetOffset..]),
            buf.Slice(DumpContentHashOffset, DumpContentHashSize).ToArray());
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
///   Offset 28 — Checksum (4 bytes)     XxHash32 of the section's bytes; validated lazily by
///                                      <see cref="CacheContainerReader.TryOpenSection"/> on first read
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
