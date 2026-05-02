using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads <c>TypeAggregateIndex.bin</c> to reconstruct a <see cref="HeapIndexBuildResult"/>
/// without re-scanning the heap. Called by the fast-path check in
/// <see cref="DiskBackedObjectIndexWriter.Build"/> when both index files are present.
/// </summary>
internal static class TypeAggregateIndexReader
{
    /// <summary>
    /// Attempts to load a cached <see cref="HeapIndexBuildResult"/> from
    /// <paramref name="typeAggPath"/> and <paramref name="objectIndexPath"/>.
    /// Returns <c>false</c> if either file is missing, corrupt, or has an
    /// incompatible version — callers must fall back to a full heap scan.
    /// </summary>
    public static bool TryLoad(
        string typeAggPath,
        string objectIndexPath,
        string dumpPath,
        long   objectCount,
        out HeapIndexBuildResult? result)
    {
        result = null;
        try
        {
            return TryLoadCore(typeAggPath, objectIndexPath, dumpPath, objectCount, out result);
        }
        catch
        {
            result = null;
            return false;
        }
    }

    // ── Core load logic ────────────────────────────────────────────────────────

    private static bool TryLoadCore(
        string typeAggPath,
        string objectIndexPath,
        string dumpPath,
        long   objectCount,
        out HeapIndexBuildResult? result)
    {
        result = null;
        using var stream = new FileStream(typeAggPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

        // ── IndexHeader ──────────────────────────────────────────────────────
        if (!IndexHeader.TryRead(stream, out var header)) return false;
        if (header.Magic   != TypeAggregateIndexWriter.Magic)   return false;
        if (header.Version != TypeAggregateIndexWriter.Version) return false;

        int typeCount = (int)Math.Min(header.RecordCount, int.MaxValue);
        if (typeCount < 0) return false;

        // ── ObjectCount field (cross-check, not used over caller's value) ──
        Span<byte> buf8 = stackalloc byte[8];
        if (stream.ReadAtLeast(buf8, 8, throwOnEndOfStream: false) < 8) return false;

        // ── ExtraHeader: BucketCount(4)+ModuleCount(4)+ShapeCount(4)+Pad(4)+DumpLength(8)+DumpTimeTicks(8) ─
        Span<byte> extra = stackalloc byte[32];
        if (stream.ReadAtLeast(extra, 32, throwOnEndOfStream: false) < 32) return false;
        int bucketCount = BinaryPrimitives.ReadInt32LittleEndian(extra);
        int moduleCount = BinaryPrimitives.ReadInt32LittleEndian(extra[4..]);
        int shapeCount  = BinaryPrimitives.ReadInt32LittleEndian(extra[8..]);
        long storedLength    = BinaryPrimitives.ReadInt64LittleEndian(extra[16..]);
        long storedTimeTicks = BinaryPrimitives.ReadInt64LittleEndian(extra[24..]);

        if (bucketCount is < 0 or > 64)   return false;
        if (moduleCount is < 0 or > 65536) return false;
        if (shapeCount  < 0)               return false;

        // Validate dump identity stamp. If both stored values are 0 the stamp was not
        // available when the index was written (e.g. a permission error) — accept it.
        // Otherwise the dump's current size and mtime must match exactly.
        if (storedLength != 0 || storedTimeTicks != 0)
        {
            try
            {
                var fi = new FileInfo(dumpPath);
                if (fi.Length != storedLength || fi.LastWriteTimeUtc.Ticks != storedTimeTicks)
                    return false; // dump replaced — rebuild required
            }
            catch
            {
                return false; // cannot stat the dump — treat as mismatch
            }
        }

        // ── SizeBuckets ──────────────────────────────────────────────────────
        long[]? sizeBuckets = null;
        if (bucketCount > 0)
        {
            sizeBuckets = new long[bucketCount];
            Span<byte> bkt = stackalloc byte[8];
            for (int i = 0; i < bucketCount; i++)
            {
                if (stream.ReadAtLeast(bkt, 8, throwOnEndOfStream: false) < 8) return false;
                sizeBuckets[i] = BinaryPrimitives.ReadInt64LittleEndian(bkt);
            }
        }

        // ── TypeAggregateEntry records ────────────────────────────────────────
        var typeAggregates = new Dictionary<ulong, TypeAggregateIndexEntry>(typeCount);
        if (typeCount > 0)
        {
            const int Chunk = 4096;
            int chunkBytes = Math.Min(typeCount, Chunk) * TypeAggregateIndexWriter.TypeEntrySize;
            byte[] buf = ArrayPool<byte>.Shared.Rent(chunkBytes);
            try
            {
                int remaining = typeCount;
                while (remaining > 0)
                {
                    int batch      = Math.Min(remaining, Chunk);
                    int bytesNeeded = batch * TypeAggregateIndexWriter.TypeEntrySize;
                    int read = stream.ReadAtLeast(buf.AsSpan(0, bytesNeeded), bytesNeeded, throwOnEndOfStream: false);
                    if (read < bytesNeeded) return false;

                    for (int i = 0; i < batch; i++)
                    {
                        var e = ReadTypeEntry(buf.AsSpan(i * TypeAggregateIndexWriter.TypeEntrySize));
                        typeAggregates[e.MethodTable] = e;
                    }
                    remaining -= batch;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ── ShapeEntry records ────────────────────────────────────────────────
        IReadOnlyDictionary<ulong, TypeShapeEntry>? shapeCache = null;
        if (shapeCount > 0)
        {
            const int Chunk = 4096;
            int chunkBytes = Math.Min(shapeCount, Chunk) * TypeAggregateIndexWriter.ShapeEntrySize;
            byte[] buf = ArrayPool<byte>.Shared.Rent(chunkBytes);
            try
            {
                var shapes = new Dictionary<ulong, TypeShapeEntry>(shapeCount);
                int remaining = shapeCount;
                while (remaining > 0)
                {
                    int batch      = Math.Min(remaining, Chunk);
                    int bytesNeeded = batch * TypeAggregateIndexWriter.ShapeEntrySize;
                    int read = stream.ReadAtLeast(buf.AsSpan(0, bytesNeeded), bytesNeeded, throwOnEndOfStream: false);
                    if (read < bytesNeeded) return false;

                    for (int i = 0; i < batch; i++)
                    {
                        int    off = i * TypeAggregateIndexWriter.ShapeEntrySize;
                        ulong  mt  = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off));
                        short  rf  = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off + 8));
                        short  vf  = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off + 10));
                        shapes[mt] = new TypeShapeEntry(rf, vf);
                    }
                    remaining -= batch;
                }
                shapeCache = shapes;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ── Module records ────────────────────────────────────────────────────
        List<ModuleInfo>? modules = null;
        if (moduleCount > 0)
        {
            modules = new List<ModuleInfo>(moduleCount);
            // Reuse a single 64 KB buffer for string bytes across all modules.
            byte[] modBuf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                Span<byte> hdr8 = stackalloc byte[8];
                for (int i = 0; i < moduleCount; i++)
                {
                    if (stream.ReadAtLeast(hdr8, 8, throwOnEndOfStream: false) < 8) return false;
                    int id      = BinaryPrimitives.ReadInt32LittleEndian(hdr8);
                    int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr8[4..]);
                    int asmLen  = BinaryPrimitives.ReadUInt16LittleEndian(hdr8[6..]);

                    int total = nameLen + asmLen;
                    // Use pooled buffer if it fits; otherwise allocate a one-off.
                    byte[] strBuf = total <= modBuf.Length ? modBuf : new byte[total];
                    if (stream.ReadAtLeast(strBuf.AsSpan(0, total), total, throwOnEndOfStream: false) < total)
                        return false;

                    modules.Add(new ModuleInfo
                    {
                        Id          = id,
                        Name        = Encoding.UTF8.GetString(strBuf, 0, nameLen),
                        AssemblyName = Encoding.UTF8.GetString(strBuf, nameLen, asmLen),
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(modBuf);
            }
        }

        result = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            objectIndexPath,
            objectCount,
            Elapsed:          TimeSpan.Zero,   // elapsed not meaningful for a cache hit
            TypeAggregates:   typeAggregates,
            InMemoryEntries:  null,
            Modules:          modules,
            GlobalSizeBuckets: sizeBuckets,
            TypeShapeCache:   shapeCache,
            SatelliteWarnings: null);

        return true;
    }

    // ── Per-record deserialization ─────────────────────────────────────────────

    private static TypeAggregateIndexEntry ReadTypeEntry(ReadOnlySpan<byte> span)
    {
        ulong mt    = BinaryPrimitives.ReadUInt64LittleEndian(span);
        int   modId = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        long  count = BinaryPrimitives.ReadInt64LittleEndian(span[12..]);
        ulong tSize = BinaryPrimitives.ReadUInt64LittleEndian(span[20..]);
        long  lohCnt = BinaryPrimitives.ReadInt64LittleEndian(span[28..]);
        ulong lohSz  = BinaryPrimitives.ReadUInt64LittleEndian(span[36..]);
        ulong sAddr  = BinaryPrimitives.ReadUInt64LittleEndian(span[44..]);
        int   g0     = BinaryPrimitives.ReadInt32LittleEndian(span[52..]);
        int   g1     = BinaryPrimitives.ReadInt32LittleEndian(span[56..]);
        int   g2     = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);
        var   flags  = (TypeAggregateFlags)span[64];

        return new TypeAggregateIndexEntry(mt, modId, count, tSize, lohCnt, lohSz, sAddr, g0, g1, g2, flags);
    }
}
