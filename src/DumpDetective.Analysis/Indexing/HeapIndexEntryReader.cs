using System.Buffers.Binary;

namespace DumpDetective.Analysis.Indexing;

internal static class HeapIndexEntryReader
{
    private const int HeaderSize = 24;
    private const int RecordSize = sizeof(ulong) * 3;

    public static IEnumerable<HeapEntry> ReadDiskEntries(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            yield break;

        using FileStream stream = new(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length <= HeaderSize)
            yield break;

        stream.Position = HeaderSize;

        byte[] buffer = new byte[RecordSize];
        while (stream.Position + RecordSize <= stream.Length)
        {
            int read = stream.Read(buffer, 0, RecordSize);
            if (read != RecordSize)
                yield break;

            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
            ulong methodTable = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16, 8));
            yield return new HeapEntry(address, methodTable, size);
        }
    }
}
