using DumpDetective.Analysis.Indexing.Container;
using System.Buffers.Binary;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Tracks the top-100 LOH objects by size during Phase 1 heap scan, then writes
/// <c>LargeObjectIndex.bin</c>.
/// </summary>
/// <remarks>
/// Record layout (24 bytes, little-endian):
///   Address (8) | MT (8) | Size (8)
/// Fixed file size: 2.4 KB (always exactly ≤ 100 entries).
/// Consumers: LohFragmentationAnalyzer, ArrayAnalyzer
/// </remarks>
internal sealed class LargeObjectTracker
{
    // File magic: "LOIX" = Large Object Index
    private const int Magic = 0x58494F4C;
    private const int Version = 1;
    private const int RecordSize = 24; // 8 + 8 + 8
    private const int MaxEntries = 100;
    private const ulong LohThreshold = 85_000;

    // Min-heap of (Size, Address, MT) — keeps the 100 largest seen so far.
    // We use a simple sorted-insert approach; 100-entry list is trivially cheap.
    private readonly List<(ulong Size, ulong Address, ulong MethodTable)> _top = new(MaxEntries + 1);

    public void Consider(ulong address, ulong methodTable, ulong size)
    {
        if (size < LohThreshold)
            return;

        _top.Add((size, address, methodTable));

        if (_top.Count > MaxEntries)
        {
            // Remove the smallest entry to keep the list at MaxEntries.
            int minIdx = 0;
            for (int i = 1; i < _top.Count; i++)
            {
                if (_top[i].Size < _top[minIdx].Size)
                    minIdx = i;
            }
            _top.RemoveAt(minIdx);
        }
    }

    public void Write(Stream stream)
    {
        // Sort descending by size before writing.
        _top.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        new IndexHeader(Magic, Version, recordCount: _top.Count).WriteTo(stream);

        Span<byte> rec = stackalloc byte[RecordSize];
        foreach ((ulong size, ulong address, ulong mt) in _top)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rec, address);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[8..], mt);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[16..], size);
            stream.Write(rec);
        }
    }

    /// <summary>
    /// Reads LargeObjectIndex.bin and invokes a processor for each record.
    /// The processor receives address, MT, size and returns whether to continue reading.
    /// </summary>
    internal static void ReadRecords(
        string containerPath,
        Action<ulong, ulong, ulong> processor,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.LargeObjects, out Stream? stream) || stream is null)
                return;

            using (stream)
            {
                if (!IndexHeader.TryRead(stream, out IndexHeader header))
                    return;

                Span<byte> rec = stackalloc byte[RecordSize];
                for (long i = 0; i < header.RecordCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                    if (read < RecordSize)
                        break;

                    ulong address = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                    ulong methodTable = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);

                    processor(address, methodTable, size);
                }
            }
        }
        catch (Exception)
        {
            // Section not found or read failed; caller continues without index.
        }
    }
}
