using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Writes <c>TypeAggregateIndex.bin</c> — a compact snapshot of all per-type aggregate
/// statistics, module registry, global size buckets, and type shape cache produced during
/// Phase 1. When this file exists alongside <c>ObjectIndex.bin</c> the entire Phase 1
/// heap scan is skipped on subsequent analyses of the same dump.
/// </summary>
/// <remarks>
/// File layout (little-endian throughout, offsets shown assuming BucketCount=8):
/// <code>
///   [  0 –  23]  IndexHeader (24 B)  Magic=0x47415954, Version=4, RecordCount=numTypes
///   [ 24 –  31]  ObjectCount (8 B)   total objects from the scan
///   [ 32 –  63]  ExtraHeader (32 B)  BucketCount(4)+ModuleCount(4)+ShapeCount(4)+Pad(4)+
///                                   Reserved(8)+Reserved(8)
///   [ 64 – 127]  SizeBuckets (64 B)  BucketCount × 8-byte signed counters
///   [128 –   …]  TypeEntry records   numTypes × 88 B each (see TypeAggregateIndexEntry doc)
///   [  … –   …]  ShapeEntry records  ShapeCount × 16 B each (MT(8)+Ref(2)+Val(2)+Pad(4))
///   [  … –  ∞]  Module records      variable: Id(4)+NameLen(2)+AsmLen(2)+Name(N)+Asm(M)
/// </code>
/// The file is written last in Phase 1 (after all satellite files). Its presence is
/// therefore a reliable indicator that the full initial build completed successfully.
/// Dump identity is validated once at the container level (see
/// <c>DumpContentHasher</c>/<c>CacheContainerReader.MatchesDumpContent</c>) before this
/// section is ever opened, so the two reserved fields above are no longer a stamp.
/// </remarks>
internal static class TypeAggregateIndexWriter
{
    // File magic: "TYAG" = Type Aggregate Index
    internal const int Magic = 0x47415954;
    // Bumped 3 -> 4 when Gen2TotalSize was added to TypeEntry (P2-3). Older TypeAggregateIndex.bin
    // files fail the header-version check in TypeAggregateIndexReader and are rebuilt via a full
    // heap rescan — the same policy used for every prior bump of this format.
    internal const int Version = 4;

    // TypeAggregateIndexEntry binary record — must match the layout in the doc comment of
    // TypeAggregateIndexEntry.cs: MT(8)+ModuleId(4)+Count(8)+TotalSize(8)+LohCount(8)+
    // LohSize(8)+SampleAddress(8)+Gen0Count(8)+Gen1Count(8)+Gen2Count(8)+Gen2TotalSize(8)+Flags(1)+Pad(3)
    internal const int TypeEntrySize = 88;

    // TypeShapeEntry binary record: MT(8)+RefFields(2)+ValFields(2)+Pad(4)
    internal const int ShapeEntrySize = 16;

    // ── Public write entry point ───────────────────────────────────────────────

    public static void Write(
        Stream stream,
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> typeAggregates,
        IReadOnlyList<ModuleInfo>? modules,
        long[]? sizeBuckets,
        IReadOnlyDictionary<ulong, TypeShapeEntry>? shapeCache,
        long objectCount)
    {
        int typeCount = typeAggregates.Count;
        int bucketCount = sizeBuckets?.Length ?? 0;
        int moduleCount = modules?.Count ?? 0;
        int shapeCount = shapeCache?.Count ?? 0;

        // ── IndexHeader ──────────────────────────────────────────────────────
        new IndexHeader(Magic, Version, recordCount: typeCount).WriteTo(stream);

        // ── ObjectCount ──────────────────────────────────────────────────────
        Span<byte> buf8 = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf8, objectCount);
        stream.Write(buf8);

        // ── ExtraHeader: BucketCount(4)+ModuleCount(4)+ShapeCount(4)+Pad(4)+Reserved(8)+Reserved(8) ─
        // The last two 8-byte fields used to carry a dump length/mtime stamp; that check is now
        // done once at the container level (see DumpContentHasher) before this section is ever
        // parsed, so they're left zero-filled here for layout compatibility.
        Span<byte> extra = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(extra, bucketCount);
        BinaryPrimitives.WriteInt32LittleEndian(extra[4..], moduleCount);
        BinaryPrimitives.WriteInt32LittleEndian(extra[8..], shapeCount);
        BinaryPrimitives.WriteInt32LittleEndian(extra[12..], 0); // pad
        BinaryPrimitives.WriteInt64LittleEndian(extra[16..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(extra[24..], 0);
        stream.Write(extra);

        // ── SizeBuckets ──────────────────────────────────────────────────────
        if (sizeBuckets is not null)
        {
            Span<byte> bkt = stackalloc byte[8];
            foreach (long v in sizeBuckets)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bkt, v);
                stream.Write(bkt);
            }
        }

        // ── TypeAggregateEntry records ────────────────────────────────────────
        if (typeCount > 0)
        {
            const int ChunkEntries = 4096;
            byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkEntries * TypeEntrySize);
            try
            {
                int off = 0;
                foreach ((ulong mt, TypeAggregateIndexEntry e) in typeAggregates)
                {
                    WriteTypeEntry(buf.AsSpan(off, TypeEntrySize), mt, e);
                    off += TypeEntrySize;
                    if (off + TypeEntrySize > buf.Length)
                    {
                        stream.Write(buf, 0, off);
                        off = 0;
                    }
                }
                if (off > 0) stream.Write(buf, 0, off);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ── ShapeEntry records ────────────────────────────────────────────────
        if (shapeCache is not null && shapeCount > 0)
        {
            const int ChunkShapes = 4096;
            byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkShapes * ShapeEntrySize);
            try
            {
                int off = 0;
                foreach ((ulong mt, TypeShapeEntry s) in shapeCache)
                {
                    WriteShapeEntry(buf.AsSpan(off, ShapeEntrySize), mt, s);
                    off += ShapeEntrySize;
                    if (off + ShapeEntrySize > buf.Length)
                    {
                        stream.Write(buf, 0, off);
                        off = 0;
                    }
                }
                if (off > 0) stream.Write(buf, 0, off);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ── Module records (variable-length strings) ──────────────────────────
        if (modules is not null && moduleCount > 0)
        {
            byte[] modBuf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int off = 0;
                foreach (ModuleInfo m in modules)
                {
                    string name = m.Name ?? string.Empty;
                    string asmName = m.AssemblyName ?? string.Empty;

                    int nameLen = Encoding.UTF8.GetByteCount(name);
                    int asmLen = Encoding.UTF8.GetByteCount(asmName);
                    int recSize = 4 + 2 + 2 + nameLen + asmLen; // Id + 2×len fields + strings

                    // Flush current buffer contents if this record would overflow.
                    if (off > 0 && off + recSize > modBuf.Length)
                    {
                        stream.Write(modBuf, 0, off);
                        off = 0;
                    }

                    if (recSize > modBuf.Length)
                    {
                        // Pathological: module name > 64 KB — write directly.
                        byte[] big = new byte[recSize];
                        WriteModuleRecord(big, 0, m.Id, name, asmName, nameLen, asmLen);
                        stream.Write(big, 0, recSize);
                        continue;
                    }

                    WriteModuleRecord(modBuf, off, m.Id, name, asmName, nameLen, asmLen);
                    off += recSize;
                }
                if (off > 0) stream.Write(modBuf, 0, off);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(modBuf);
            }
        }

        stream.Flush();
    }

    // ── Per-record serialization helpers ──────────────────────────────────────

    private static void WriteTypeEntry(Span<byte> span, ulong mt, TypeAggregateIndexEntry e)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span, mt);              //  8 → total  8
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], e.ModuleId);     //  4 → total 12
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], e.Count);        //  8 → total 20
        BinaryPrimitives.WriteUInt64LittleEndian(span[20..], e.TotalSize);    //  8 → total 28
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], e.LohCount);     //  8 → total 36
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], e.LohSize);      //  8 → total 44
        BinaryPrimitives.WriteUInt64LittleEndian(span[44..], e.SampleAddress);//  8 → total 52
        BinaryPrimitives.WriteInt64LittleEndian(span[52..], e.Gen0Count);    //  8 → total 60
        BinaryPrimitives.WriteInt64LittleEndian(span[60..], e.Gen1Count);    //  8 → total 68
        BinaryPrimitives.WriteInt64LittleEndian(span[68..], e.Gen2Count);    //  8 → total 76
        BinaryPrimitives.WriteUInt64LittleEndian(span[76..], e.Gen2TotalSize); //  8 → total 84
        span[84] = (byte)e.Flags;                                              //  1 → total 85
        span[85] = span[86] = span[87] = 0;                                    //  3 → total 88
    }

    private static void WriteShapeEntry(Span<byte> span, ulong mt, TypeShapeEntry s)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span, mt);            // 8
        BinaryPrimitives.WriteInt16LittleEndian(span[8..], s.RefFields);  // 2
        BinaryPrimitives.WriteInt16LittleEndian(span[10..], s.ValFields);  // 2
        span[12] = span[13] = span[14] = span[15] = 0;                     // 4 pad
    }

    private static void WriteModuleRecord(
        byte[] buf, int off,
        int id, string name, string asmName, int nameLen, int asmLen)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), id);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), (ushort)nameLen);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 6), (ushort)asmLen);
        Encoding.UTF8.GetBytes(name, buf.AsSpan(off + 8));
        Encoding.UTF8.GetBytes(asmName, buf.AsSpan(off + 8 + nameLen));
    }
}
