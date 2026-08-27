using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads the <c>TypeAggregates</c> (plus <c>StringDedup</c>/<c>StringDedupMeta</c>) sections of
/// <c>cache.bin</c> to reconstruct a <see cref="HeapIndexBuildResult"/> without re-scanning the
/// heap. Called by the fast-path check in <see cref="DiskBackedObjectIndexWriter.Build"/> when the
/// container's <c>TypeAggregates</c> section is present.
/// </summary>
internal static class TypeAggregateIndexReader
{
    /// <summary>
    /// Attempts to load a cached <see cref="HeapIndexBuildResult"/> from the container's
    /// <c>TypeAggregates</c> section (plus the optional <c>StringDedup</c>/<c>StringDedupMeta</c>
    /// sections). Returns <c>false</c> if the section is missing, corrupt, or has an incompatible
    /// version — callers must fall back to a full heap scan.
    /// </summary>
    public static bool TryLoad(
        CacheContainerReader reader,
        string containerPath,
        long objectCount,
        out HeapIndexBuildResult? result)
    {
        result = null;
        try
        {
            return TryLoadCore(reader, containerPath, objectCount, out result);
        }
        catch
        {
            result = null;
            return false;
        }
    }

    // ── Core load logic ────────────────────────────────────────────────────────

    private static bool TryLoadCore(
        CacheContainerReader reader,
        string containerPath,
        long objectCount,
        out HeapIndexBuildResult? result)
    {
        result = null;
        if (!reader.TryOpenSection(CacheSectionId.TypeAggregates, out Stream? sectionStream) || sectionStream is null)
            return false;
        using var stream = sectionStream;

        // ── IndexHeader ──────────────────────────────────────────────────────
        if (!IndexHeader.TryRead(stream, out var header)) return false;
        if (header.Magic != TypeAggregateIndexWriter.Magic) return false;
        if (header.Version != TypeAggregateIndexWriter.Version) return false;

        int typeCount = (int)Math.Min(header.RecordCount, int.MaxValue);
        if (typeCount < 0) return false;

        // ── ObjectCount field (cross-check, not used over caller's value) ──
        Span<byte> buf8 = stackalloc byte[8];
        if (stream.ReadAtLeast(buf8, 8, throwOnEndOfStream: false) < 8) return false;

        // ── ExtraHeader: BucketCount(4)+ModuleCount(4)+ShapeCount(4)+Pad(4)+Reserved(8)+Reserved(8) ─
        // Trailing 16 bytes used to be a dump length/mtime stamp; that check now happens once at
        // the container level (see DumpContentHasher/CacheContainerReader.MatchesDumpContent)
        // before this section is ever opened, so they're read past but otherwise unused.
        Span<byte> extra = stackalloc byte[32];
        if (stream.ReadAtLeast(extra, 32, throwOnEndOfStream: false) < 32) return false;
        int bucketCount = BinaryPrimitives.ReadInt32LittleEndian(extra);
        int moduleCount = BinaryPrimitives.ReadInt32LittleEndian(extra[4..]);
        int shapeCount = BinaryPrimitives.ReadInt32LittleEndian(extra[8..]);

        if (bucketCount is < 0 or > 64) return false;
        if (moduleCount is < 0 or > 65536) return false;
        if (shapeCount < 0) return false;

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
                    int batch = Math.Min(remaining, Chunk);
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
                    int batch = Math.Min(remaining, Chunk);
                    int bytesNeeded = batch * TypeAggregateIndexWriter.ShapeEntrySize;
                    int read = stream.ReadAtLeast(buf.AsSpan(0, bytesNeeded), bytesNeeded, throwOnEndOfStream: false);
                    if (read < bytesNeeded) return false;

                    for (int i = 0; i < batch; i++)
                    {
                        int off = i * TypeAggregateIndexWriter.ShapeEntrySize;
                        ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off));
                        short rf = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off + 8));
                        short vf = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off + 10));
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
                    int id = BinaryPrimitives.ReadInt32LittleEndian(hdr8);
                    int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr8[4..]);
                    int asmLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr8[6..]);

                    int total = nameLen + asmLen;
                    // Use pooled buffer if it fits; otherwise allocate a one-off.
                    byte[] strBuf = total <= modBuf.Length ? modBuf : new byte[total];
                    if (stream.ReadAtLeast(strBuf.AsSpan(0, total), total, throwOnEndOfStream: false) < total)
                        return false;

                    modules.Add(new ModuleInfo
                    {
                        Id = id,
                        Name = Encoding.UTF8.GetString(strBuf, 0, nameLen),
                        AssemblyName = Encoding.UTF8.GetString(strBuf, nameLen, asmLen),
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(modBuf);
            }
        }

        // Attempt to load optional StringDedup/StringDedupMeta sections
        IReadOnlyDictionary<ulong, StringDedupEntry>? stringDedup = null;
        DistributionSummary? stringDedupDistribution = null;
        try
        {
            if (reader.TryOpenSection(CacheSectionId.StringDedup, out Stream? dedupStream) && dedupStream is not null)
            {
                using var ds = dedupStream;
                Span<byte> hdr = stackalloc byte[12];
                if (ds.ReadAtLeast(hdr, 12, throwOnEndOfStream: false) != 12) throw new InvalidDataException("short header");
                int magic = BinaryPrimitives.ReadInt32LittleEndian(hdr);
                int version = BinaryPrimitives.ReadInt32LittleEndian(hdr[4..]);
                int entries = BinaryPrimitives.ReadInt32LittleEndian(hdr[8..]);
                if (magic == 0x50554453 && version == 1 && entries > 0)
                {
                    var map = new Dictionary<ulong, StringDedupEntry>(entries);
                    Span<byte> rec = stackalloc byte[31];
                    Span<byte> addrBuf = stackalloc byte[8];
                    for (int i = 0; i < entries; i++)
                    {
                        if (ds.ReadAtLeast(rec, rec.Length, throwOnEndOfStream: false) != rec.Length) throw new InvalidDataException("short record");
                        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                        int cnt = BinaryPrimitives.ReadInt32LittleEndian(rec[8..]);
                        ulong totalSize = BinaryPrimitives.ReadUInt64LittleEndian(rec[12..]);
                        ulong dominantMt = BinaryPrimitives.ReadUInt64LittleEndian(rec[20..]);
                        int sampleCount = rec[28];
                        ushort previewLen = BinaryPrimitives.ReadUInt16LittleEndian(rec[29..]);

                        ulong[]? samples = null;
                        if (sampleCount > 0)
                        {
                            samples = new ulong[Math.Min(2, sampleCount)];
                            for (int s = 0; s < Math.Min(2, sampleCount); s++)
                            {
                                if (ds.ReadAtLeast(addrBuf, 8, throwOnEndOfStream: false) != 8) throw new InvalidDataException("short addr");
                                samples[s] = BinaryPrimitives.ReadUInt64LittleEndian(addrBuf);
                            }
                        }

                        string preview = string.Empty;
                        if (previewLen > 0)
                        {
                            byte[] pbuf = new byte[previewLen];
                            if (ds.ReadAtLeast(pbuf, previewLen, throwOnEndOfStream: false) != previewLen) throw new InvalidDataException("short preview");
                            preview = System.Text.Encoding.UTF8.GetString(pbuf);
                        }

                        var entry = new StringDedupEntry(preview, totalSize, samples is not null && samples.Length > 0 ? samples[0] : 0, dominantMt);
                        entry.Count = cnt;
                        entry.SampleAddresses = samples;
                        map[hash] = entry;
                    }
                    stringDedup = map.Count > 0 ? map : null;
                }
            }

            // Attempt to load optional StringDedupMeta section (opaque UTF-8 JSON bytes)
            try
            {
                if (reader.TryOpenSection(CacheSectionId.StringDedupMeta, out Stream? metaStream) && metaStream is not null)
                {
                    using var ms = metaStream;
                    using var textReader = new StreamReader(ms, Encoding.UTF8);
                    string txt = textReader.ReadToEnd();
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    stringDedupDistribution = System.Text.Json.JsonSerializer.Deserialize<DistributionSummary>(txt, opts);
                }
            }
            catch { stringDedupDistribution = null; }
        }
        catch { stringDedup = null; }

        result = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            containerPath,
            objectCount,
            Elapsed: TimeSpan.Zero,   // elapsed not meaningful for a cache hit
            TypeAggregates: typeAggregates,
            InMemoryEntries: null,
            Modules: modules,
            GlobalSizeBuckets: sizeBuckets,
            TypeShapeCache: shapeCache,
            SatelliteWarnings: null,
            StringDedupIndex: stringDedup,
            StringDedupDistribution: stringDedupDistribution);

        return true;
    }

    // ── Per-record deserialization ─────────────────────────────────────────────

    private static TypeAggregateIndexEntry ReadTypeEntry(ReadOnlySpan<byte> span)
    {
        ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(span);
        int modId = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        long count = BinaryPrimitives.ReadInt64LittleEndian(span[12..]);
        ulong tSize = BinaryPrimitives.ReadUInt64LittleEndian(span[20..]);
        long lohCnt = BinaryPrimitives.ReadInt64LittleEndian(span[28..]);
        ulong lohSz = BinaryPrimitives.ReadUInt64LittleEndian(span[36..]);
        ulong sAddr = BinaryPrimitives.ReadUInt64LittleEndian(span[44..]);
        long g0 = BinaryPrimitives.ReadInt64LittleEndian(span[52..]);
        long g1 = BinaryPrimitives.ReadInt64LittleEndian(span[60..]);
        long g2 = BinaryPrimitives.ReadInt64LittleEndian(span[68..]);
        ulong g2TotalSize = BinaryPrimitives.ReadUInt64LittleEndian(span[76..]);
        var flags = (TypeAggregateFlags)span[84];

        return new TypeAggregateIndexEntry(mt, modId, count, tSize, lohCnt, lohSz, sAddr, g0, g1, g2, flags, g2TotalSize);
    }
}
