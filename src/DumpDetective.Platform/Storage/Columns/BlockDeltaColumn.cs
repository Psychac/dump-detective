using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;

using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Platform.Storage.Columns;

/// <summary>
/// Decodes an address column stored as a 4-byte delta from a per-block base
/// (docs/cache/cache-format-clean-slate-redesign.md §10.3). Blocks are a fixed
/// <see cref="BlockRecords"/> records, so a record's block is a shift rather than a search, and
/// both the streaming and the binary-search read paths stay O(1) per record.
/// </summary>
/// <remarks>
/// The encoding assumes nothing about the column beyond its record count. A descending step, an
/// unaligned address or a block spanning more than 4 GB all produce a delta that doesn't fit, and
/// escape to <see cref="ColumnOverflowTable"/> — which is why this needs no per-dump width choice
/// the way <see cref="NarrowColumnWidth"/> does for value columns.
/// </remarks>
internal sealed class BlockDeltaColumn
{
    public const int BlockShift = 10;
    public const int BlockRecords = 1 << BlockShift;
    public const int DeltaWidth = sizeof(uint);
    public const uint EscapeSentinel = uint.MaxValue;

    private readonly ulong[] _blockBases;
    private readonly ColumnOverflowTable _overflow;

    private BlockDeltaColumn(ulong[] blockBases, ColumnOverflowTable overflow)
    {
        _blockBases = blockBases;
        _overflow = overflow;
    }

    /// <summary>
    /// Loads the base array and escape table. Returns <c>false</c> if either is missing or if the
    /// base count doesn't cover <paramref name="recordCount"/> — a delta column without its bases
    /// decodes to nonsense addresses, so it has to be a failed open rather than a degraded one.
    /// </summary>
    public static bool TryLoad(
        CacheContainerReader container,
        CacheSectionId blockBasesSectionId,
        CacheSectionId overflowSectionId,
        long recordCount,
        out BlockDeltaColumn? column)
    {
        column = null;

        if (!container.TryOpenSectionAccessor(blockBasesSectionId, out MemoryMappedViewAccessor? accessor, out long length)
            || accessor is null
            || length % sizeof(ulong) != 0)
        {
            accessor?.Dispose();
            return false;
        }

        ulong[] blockBases;
        using (accessor)
        {
            blockBases = new ulong[length / sizeof(ulong)];
            for (int i = 0; i < blockBases.Length; i++)
                blockBases[i] = accessor.ReadUInt64(i * (long)sizeof(ulong));
        }

        if (blockBases.Length != BlockCountFor(recordCount))
            return false;

        if (!ColumnOverflowTable.TryLoad(container, overflowSectionId, out ColumnOverflowTable? overflow) || overflow is null)
            return false;

        column = new BlockDeltaColumn(blockBases, overflow);
        return true;
    }

    public static int BlockCountFor(long recordCount) => (int)((recordCount + BlockRecords - 1) / BlockRecords);

    /// <summary>Point-path decode; the escape branch costs a binary search over a table of thousands.</summary>
    public ulong Decode(uint delta, long recordIndex) =>
        delta == EscapeSentinel && _overflow.TryGetValue(recordIndex, out ulong escaped)
            ? escaped
            : _blockBases[recordIndex >> BlockShift] + delta;

    /// <summary>Streaming-path decode; the escape branch advances a cursor instead of searching.</summary>
    public ulong Decode(uint delta, long recordIndex, ref ColumnOverflowTable.Cursor cursor) =>
        delta == EscapeSentinel
            ? cursor.Read(recordIndex)
            : _blockBases[recordIndex >> BlockShift] + delta;

    public ColumnOverflowTable.Cursor OpenCursor(long startRecordIndex) => _overflow.OpenCursor(startRecordIndex);

    /// <summary>
    /// Narrows an ascending column to the one block that can contain <paramref name="value"/>, by
    /// binary searching the resident block bases. Returns <c>false</c> when the value precedes the
    /// first block, i.e. cannot be present.
    /// </summary>
    /// <remarks>
    /// This is the resident half of <see cref="MonotonicAddressColumn"/>'s rank search, and the
    /// reason it costs 0.65 MiB instead of 2,325.9 MB: the bases are ~85K entries on the largest
    /// measured dump, so this half stays in cache at any dump size, and only the in-block probe
    /// that follows touches a mapped page.
    ///
    /// A block's base is its *first* value, and the column ascends, so the last base at or below
    /// the target names the only block that can hold it. Escaped records don't affect this — their
    /// block base is still a real first-value.
    /// </remarks>
    public bool TryFindBlock(ulong value, long recordCount, out long blockStart, out long blockEnd)
    {
        blockStart = 0;
        blockEnd = -1;

        int lo = 0;
        int hi = _blockBases.Length - 1;
        int block = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_blockBases[mid] <= value)
            {
                block = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (block < 0)
            return false;

        blockStart = (long)block << BlockShift;
        blockEnd = Math.Min(blockStart + BlockRecords, recordCount) - 1;
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> for <paramref name="recordIndex"/>, appending to
    /// <paramref name="blockBases"/> when a new block starts and to <paramref name="overflow"/> when
    /// the delta doesn't fit. Written as a free function so the writer can call it inside its
    /// streaming copy loop without materialising the column.
    /// </summary>
    public static uint Encode(
        ulong value,
        long recordIndex,
        List<ulong> blockBases,
        List<(uint RecordIndex, ulong Value)> overflow)
    {
        if ((recordIndex & (BlockRecords - 1)) == 0)
            blockBases.Add(value);

        ulong delta = value - blockBases[^1];
        if (delta >= EscapeSentinel)
        {
            overflow.Add(((uint)recordIndex, value));
            return EscapeSentinel;
        }

        return (uint)delta;
    }

    /// <summary>Writes the block-base array as its own section body, returning its checksum.</summary>
    public static uint WriteBlockBases(Stream stream, List<ulong> blockBases, int bufferSize)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int perChunk = buffer.Length / sizeof(ulong);
            for (int start = 0; start < blockBases.Count; start += perChunk)
            {
                int count = Math.Min(perChunk, blockBases.Count - start);
                for (int i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(i * sizeof(ulong)), blockBases[start + i]);

                int bytes = count * sizeof(ulong);
                stream.Write(buffer, 0, bytes);
                hasher.Append(buffer.AsSpan(0, bytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }
}
