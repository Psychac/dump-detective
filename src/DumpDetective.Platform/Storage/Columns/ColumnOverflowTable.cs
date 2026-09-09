using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;

using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Platform.Storage.Columns;

/// <summary>
/// The escape side-table of a narrowed column — the values that did not fit the column's stored
/// width, keyed by record index (docs/cache/cache-format-clean-slate-redesign.md §10.1). Sorted
/// ascending by record index, which is what lets a streaming reader walk it with a cursor instead
/// of searching per escaped record.
/// </summary>
/// <remarks>
/// Held in managed arrays rather than left mapped: the measured population is 0.026–0.037% of
/// records (§13.2 of the measurements doc), so even the 87.1M-object dump lands around 380 KB, and
/// both access paths want random access to it without a mapped view's per-read bounds check.
/// </remarks>
internal sealed class ColumnOverflowTable
{
    public const int EntrySize = sizeof(uint) + sizeof(ulong);

    public static readonly ColumnOverflowTable Empty = new([], []);

    private readonly uint[] _recordIndices;
    private readonly ulong[] _values;

    private ColumnOverflowTable(uint[] recordIndices, ulong[] values)
    {
        _recordIndices = recordIndices;
        _values = values;
    }

    public int Count => _recordIndices.Length;

    /// <summary>
    /// Loads the table from <paramref name="sectionId"/>. Returns <c>false</c> only when the section
    /// is absent from the container or fails its checksum — a caller reading a narrowed column must
    /// treat that as a cold cache, because the escaped records are unrecoverable without it. A
    /// present-but-empty section is a success returning <see cref="Empty"/>.
    /// </summary>
    public static bool TryLoad(CacheContainerReader container, CacheSectionId sectionId, out ColumnOverflowTable? table)
    {
        table = null;

        if (!container.TryOpenSectionAccessor(sectionId, out MemoryMappedViewAccessor? accessor, out long length))
            return false;

        if (accessor is null || length == 0)
        {
            table = Empty;
            return true;
        }

        using (accessor)
        {
            if (length % EntrySize != 0)
                return false;

            int count = (int)(length / EntrySize);
            var recordIndices = new uint[count];
            var values = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                long offset = i * (long)EntrySize;
                recordIndices[i] = accessor.ReadUInt32(offset);
                values[i] = accessor.ReadUInt64(offset + sizeof(uint));
            }

            table = new ColumnOverflowTable(recordIndices, values);
            return true;
        }
    }

    /// <summary>Point lookup for the random-access readers. Escaped records are rare, so this runs rarely.</summary>
    public bool TryGetValue(long recordIndex, out ulong value)
    {
        int lo = 0;
        int hi = _recordIndices.Length - 1;
        var key = (uint)recordIndex;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            uint candidate = _recordIndices[mid];

            if (candidate == key)
            {
                value = _values[mid];
                return true;
            }

            if (candidate < key)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// A forward-only position for streaming decode. Seeded once per enumeration range so a batch
    /// loop resolves an escape by advancing, not by searching.
    /// </summary>
    public Cursor OpenCursor(long startRecordIndex)
    {
        int lo = 0;
        int hi = _recordIndices.Length;
        var key = (uint)startRecordIndex;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_recordIndices[mid] < key)
                lo = mid + 1;
            else
                hi = mid;
        }

        return new Cursor(_recordIndices, _values, lo);
    }

    internal struct Cursor(uint[] recordIndices, ulong[] values, int position)
    {
        private readonly uint[] _recordIndices = recordIndices;
        private readonly ulong[] _values = values;
        private int _position = position;

        /// <summary>
        /// Resolves the escaped value at <paramref name="recordIndex"/>. Skips forward rather than
        /// assuming the next entry is the right one, so a caller that reads a partially-populated
        /// batch or restarts mid-range still lands on the correct row.
        /// </summary>
        public ulong Read(long recordIndex)
        {
            var key = (uint)recordIndex;
            while (_position < _recordIndices.Length && _recordIndices[_position] < key)
                _position++;

            return _position < _recordIndices.Length && _recordIndices[_position] == key
                ? _values[_position]
                : 0;
        }
    }

    /// <summary>
    /// Streams <paramref name="entries"/> into the container as the section body, returning the
    /// checksum so the caller can close the section without the re-read pass.
    /// </summary>
    public static uint Write(Stream stream, List<(uint RecordIndex, ulong Value)> entries, int bufferSize)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int perChunk = buffer.Length / EntrySize;
            for (int start = 0; start < entries.Count; start += perChunk)
            {
                int count = Math.Min(perChunk, entries.Count - start);
                for (int i = 0; i < count; i++)
                {
                    (uint recordIndex, ulong value) = entries[start + i];
                    Span<byte> slot = buffer.AsSpan(i * EntrySize);
                    BinaryPrimitives.WriteUInt32LittleEndian(slot, recordIndex);
                    BinaryPrimitives.WriteUInt64LittleEndian(slot[sizeof(uint)..], value);
                }

                int bytes = count * EntrySize;
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
