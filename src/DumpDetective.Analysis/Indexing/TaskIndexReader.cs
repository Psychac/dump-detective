using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal static class TaskIndexReader
{
    private const int TaskRecordSize = 20; // Address(8) | MT(8) | StateFlags(4)
    private const int TaskHeaderMagic = 0x58494B54; // "TKIX"
    private const int TaskHeaderVersion = 1;

    public static List<(ulong Address, ulong Mt, int StateFlags)>? ReadTaskIndexFile(
        string containerPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
                return null;

            if (!reader.TryOpenSection(CacheSectionId.Tasks, out Stream? sectionStream) || sectionStream is null)
                return null;

            using Stream stream = sectionStream;

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return null;

            if (!header.IsValid(TaskHeaderMagic, TaskHeaderVersion))
                return null;

            long recordCount = header.RecordCount;
            var result = new List<(ulong, ulong, int)>(capacity: (int)Math.Min(recordCount, int.MaxValue));

            Span<byte> rec = stackalloc byte[TaskRecordSize];
            for (long i = 0; i < recordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.ReadAtLeast(rec, TaskRecordSize, throwOnEndOfStream: false) < TaskRecordSize)
                    break;

                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                int stateFlags = BinaryPrimitives.ReadInt32LittleEndian(rec[16..]);

                result.Add((address, mt, stateFlags));
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
