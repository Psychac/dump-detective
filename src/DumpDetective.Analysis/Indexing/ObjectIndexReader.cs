using System.Buffers;
using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal sealed class ObjectIndexReader : IObjectIndexReader
{
    /// <summary>Shared singleton — stateless, safe to reuse across calls.</summary>
    internal static readonly ObjectIndexReader Instance = new();

    private const int HeaderSize = 24;
    private const int RecordSize = sizeof(ulong) * 3;

    public IEnumerable<HeapEntry> ReadEntries(string containerPath)
    {
        return ReadDiskEntries(containerPath);
    }

    // Internal static helper kept for call sites that don't need DI.
    internal static IEnumerable<HeapEntry> ReadDiskEntries(string containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            yield break;
        if (!reader.TryOpenSection(CacheSectionId.Objects, out Stream? sectionStream) || sectionStream is null)
            yield break;

        using Stream stream = sectionStream;
        if (stream.Length <= HeaderSize)
            yield break;

        stream.Position = HeaderSize;

        // Choose a batch size relative to the index size for efficient reads.
        long indexBytes = stream.Length - HeaderSize;
        int batchSize = indexBytes > 4L * 1024 * 1024 * 1024 ? 8 * 1024 * 1024 :
                        indexBytes > 512L * 1024 * 1024 ? 2 * 1024 * 1024 :
                        256 * 1024;

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(batchSize);
        try
        {
            // carryOver holds a record fragment left over from the previous read that
            // straddled a batch boundary — it's kept at the front of the buffer and
            // completed by the next read rather than discarded.
            int carryOver = 0;
            int bytesRead;
            while ((bytesRead = stream.Read(readBuffer, carryOver, batchSize - carryOver)) > 0)
            {
                int total = carryOver + bytesRead;
                int offset = 0;
                while (offset + RecordSize <= total)
                {
                    ulong address = BinaryPrimitives.ReadUInt64LittleEndian(readBuffer.AsSpan(offset, 8));
                    ulong methodTable = BinaryPrimitives.ReadUInt64LittleEndian(readBuffer.AsSpan(offset + 8, 8));
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(readBuffer.AsSpan(offset + 16, 8));
                    yield return new HeapEntry(address, methodTable, size);
                    offset += RecordSize;
                }

                carryOver = total - offset;
                if (carryOver > 0)
                    Buffer.BlockCopy(readBuffer, offset, readBuffer, 0, carryOver);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
