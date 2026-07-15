using System.Buffers;
using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal sealed class ObjectIndexReader : IObjectIndexReader
{
    /// <summary>Shared singleton — stateless, safe to reuse across calls.</summary>
    internal static readonly ObjectIndexReader Instance = new();

    private const int ColumnSize = sizeof(ulong);

    public IEnumerable<HeapEntry> ReadEntries(string containerPath)
    {
        return ReadDiskEntries(containerPath);
    }

    // Internal static helper kept for call sites that don't need DI.
    internal static IEnumerable<HeapEntry> ReadDiskEntries(string containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            yield break;

        if (!reader.TryOpenSection(CacheSectionId.ObjectAddresses, out Stream? addrStream) || addrStream is null)
            yield break;
        if (!reader.TryOpenSection(CacheSectionId.ObjectMethodTables, out Stream? mtStream) || mtStream is null)
        {
            addrStream.Dispose();
            yield break;
        }
        if (!reader.TryOpenSection(CacheSectionId.ObjectSizes, out Stream? sizeStream) || sizeStream is null)
        {
            addrStream.Dispose();
            mtStream.Dispose();
            yield break;
        }

        using Stream addr = addrStream;
        using Stream mt = mtStream;
        using Stream size = sizeStream;

        long recordCount = addr.Length / ColumnSize;
        if (recordCount == 0 || mt.Length / ColumnSize != recordCount || size.Length / ColumnSize != recordCount)
            yield break;

        // Choose a batch size (in records) relative to the index size for efficient reads —
        // each record costs 3x ColumnSize bytes total across the three column streams.
        long indexBytes = addr.Length + mt.Length + size.Length;
        int batchBytes = indexBytes > 4L * 1024 * 1024 * 1024 ? 8 * 1024 * 1024 :
                        indexBytes > 512L * 1024 * 1024 ? 2 * 1024 * 1024 :
                        256 * 1024;
        int batchRecords = Math.Max(batchBytes / (ColumnSize * 3), 1);

        byte[] addrBuf = ArrayPool<byte>.Shared.Rent(batchRecords * ColumnSize);
        byte[] mtBuf = ArrayPool<byte>.Shared.Rent(batchRecords * ColumnSize);
        byte[] sizeBuf = ArrayPool<byte>.Shared.Rent(batchRecords * ColumnSize);
        try
        {
            long remaining = recordCount;
            while (remaining > 0)
            {
                int chunkRecords = (int)Math.Min(batchRecords, remaining);
                int chunkBytes = chunkRecords * ColumnSize;

                ReadColumnFully(addr, addrBuf, chunkBytes);
                ReadColumnFully(mt, mtBuf, chunkBytes);
                ReadColumnFully(size, sizeBuf, chunkBytes);

                for (int i = 0; i < chunkRecords; i++)
                {
                    int off = i * ColumnSize;
                    ulong address = BinaryPrimitives.ReadUInt64LittleEndian(addrBuf.AsSpan(off, 8));
                    ulong methodTable = BinaryPrimitives.ReadUInt64LittleEndian(mtBuf.AsSpan(off, 8));
                    ulong objSize = BinaryPrimitives.ReadUInt64LittleEndian(sizeBuf.AsSpan(off, 8));
                    yield return new HeapEntry(address, methodTable, objSize);
                }

                remaining -= chunkRecords;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(addrBuf);
            ArrayPool<byte>.Shared.Return(mtBuf);
            ArrayPool<byte>.Shared.Return(sizeBuf);
        }
    }

    private static void ReadColumnFully(Stream stream, byte[] buffer, int byteCount)
    {
        int total = 0;
        while (total < byteCount)
        {
            int read = stream.Read(buffer, total, byteCount - total);
            if (read <= 0)
                throw new EndOfStreamException("Columnar object index section ended before expected record count.");
            total += read;
        }
    }
}
