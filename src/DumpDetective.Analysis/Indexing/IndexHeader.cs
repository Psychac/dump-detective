using System.Buffers.Binary;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Shared 24-byte binary file header written at offset 0 of every satellite index file.
/// </summary>
/// <remarks>
/// Layout (little-endian, all fields):
///   Offset  0 — Magic (4 bytes)       file-type identifier, unique per file kind
///   Offset  4 — Version (4 bytes)     format version; readers reject unsupported versions
///   Offset  8 — RecordCount (8 bytes) number of data records following the header
///   Offset 16 — Reserved (8 bytes)    reserved for future use (written as 0)
/// Total = 24 bytes
///
/// Every writer produces this header; every reader validates Magic + Version before consuming
/// records. This ensures stale or truncated index files are rejected cleanly.
/// </remarks>
internal readonly struct IndexHeader
{
    public const int Size = 24;

    public readonly int Magic;
    public readonly int Version;
    public readonly long RecordCount;
    public readonly long Reserved;

    public IndexHeader(int magic, int version, long recordCount)
    {
        Magic = magic;
        Version = version;
        RecordCount = recordCount;
        Reserved = 0;
    }

    /// <summary>Writes this header into the first <see cref="Size"/> bytes of <paramref name="stream"/>.</summary>
    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(buf[8..], RecordCount);
        BinaryPrimitives.WriteInt64LittleEndian(buf[16..], Reserved);
        stream.Write(buf);
    }

    /// <summary>
    /// Reads the header from the current stream position (must be at offset 0).
    /// Returns <c>false</c> if the stream is too short to contain a full header.
    /// </summary>
    public static bool TryRead(Stream stream, out IndexHeader header)
    {
        Span<byte> buf = stackalloc byte[Size];
        int read = stream.ReadAtLeast(buf, Size, throwOnEndOfStream: false);
        if (read < Size)
        {
            header = default;
            return false;
        }

        header = new IndexHeader(
            BinaryPrimitives.ReadInt32LittleEndian(buf),
            BinaryPrimitives.ReadInt32LittleEndian(buf[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[8..]));
        return true;
    }

    /// <summary>
    /// Seeks to offset 0 and overwrites only the <see cref="RecordCount"/> field.
    /// Call after all records are written to patch the count written during header pre-write.
    /// </summary>
    public static void PatchRecordCount(Stream stream, long recordCount)
    {
        long saved = stream.Position;
        stream.Position = 8;
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, recordCount);
        stream.Write(buf);
        stream.Position = saved;
    }

    /// <summary>Returns <c>true</c> when this header's magic and version match the expected values.</summary>
    public bool IsValid(int expectedMagic, int expectedVersion) =>
        Magic == expectedMagic && Version == expectedVersion;
}
