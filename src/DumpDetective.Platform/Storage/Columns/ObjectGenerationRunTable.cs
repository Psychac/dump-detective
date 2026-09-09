using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Platform.Storage.Columns;

/// <summary>
/// Per-object GC generation, run-length encoded: one record per *change* of generation rather than
/// one byte per object (docs/cache/cache-ideal-design.md §3.2, O2).
/// </summary>
/// <remarks>
/// Generation is piecewise-constant over the object table, because the table is concatenated in
/// segment order and a segment's gen0/gen1/gen2 sub-ranges are contiguous. Measured directly from
/// the column this replaces: **13 runs over 14,620,162 objects** on the 3.3 GB reference dump and
/// **50 over 87,104,236** on the 27.5 GB one. So a 83.07 MiB column carried roughly 600 bytes of
/// information.
///
/// The runs are built from the same per-object values the byte column used to store, during the
/// same concatenation pass that used to write it — not re-derived from segment metadata. That
/// matters: generation for an Ephemeral segment comes from <c>ClrSegment.GetGeneration</c>, and LOH
/// objects report generation 3, so reproducing it from persisted segment ranges would mean
/// reimplementing ClrMD's own rules. Encoding the answer instead of the inputs keeps this exact by
/// construction.
///
/// Record layout (12 bytes, little-endian), ascending by <c>FirstRecordIndex</c>, first record
/// always 0:
///   FirstRecordIndex (8) | Generation (1) | Pad (3)
/// </remarks>
internal sealed class ObjectGenerationRunTable
{
    private const int RecordSize = 12;

    public static readonly ObjectGenerationRunTable Empty = new([], []);

    private readonly long[] _firstRecordIndices;
    private readonly sbyte[] _generations;

    private ObjectGenerationRunTable(long[] firstRecordIndices, sbyte[] generations)
    {
        _firstRecordIndices = firstRecordIndices;
        _generations = generations;
    }

    public int RunCount => _generations.Length;

    /// <summary>
    /// Loads the table. Returns <c>false</c> when the section is absent or malformed — the caller
    /// treats that as a cold cache, because a missing generation table would otherwise silently
    /// report every object as generation 0.
    /// </summary>
    public static bool TryLoad(CacheContainerReader container, out ObjectGenerationRunTable? table)
    {
        table = null;

        // The record count comes from the TOC, not from the stream: TryOpenSection hands back a
        // MemoryMappedViewStream whose Length is rounded up to the OS allocation granularity, so it
        // is not the section's byte length. SegmentIndexWriter.ReadRecords sidesteps the same trap
        // by trusting its own header instead.
        if (!container.TryGetSectionInfo(CacheSectionId.ObjectGenerationRuns, out CacheTocEntry entry)
            || entry.Length % RecordSize != 0)
            return false;

        if (!container.TryOpenSection(CacheSectionId.ObjectGenerationRuns, out Stream? stream) || stream is null)
            return false;

        using (stream)
        {
            int count = (int)(entry.Length / RecordSize);
            if (count == 0)
            {
                table = Empty;
                return true;
            }

            var firstRecordIndices = new long[count];
            var generations = new sbyte[count];

            byte[] buffer = ArrayPool<byte>.Shared.Rent(RecordSize);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (stream.ReadAtLeast(buffer.AsSpan(0, RecordSize), RecordSize, throwOnEndOfStream: false) < RecordSize)
                        return false;

                    firstRecordIndices[i] = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                    generations[i] = unchecked((sbyte)buffer[8]);

                    if (i > 0 && firstRecordIndices[i] <= firstRecordIndices[i - 1])
                        return false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (firstRecordIndices[0] != 0)
                return false;

            table = new ObjectGenerationRunTable(firstRecordIndices, generations);
            return true;
        }
    }

    /// <summary>
    /// A cursor positioned at <paramref name="startRecordIndex"/>. Streaming readers advance one
    /// forward; the initial position costs a binary search over a handful of runs.
    /// </summary>
    public Cursor OpenCursor(long startRecordIndex)
    {
        if (_generations.Length == 0)
            return new Cursor(_firstRecordIndices, _generations, 0);

        int lo = 0;
        int hi = _firstRecordIndices.Length - 1;
        int found = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_firstRecordIndices[mid] <= startRecordIndex)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return new Cursor(_firstRecordIndices, _generations, found);
    }

    /// <summary>Point lookup, for callers without a streaming position.</summary>
    public sbyte GetGeneration(long recordIndex)
    {
        Cursor cursor = OpenCursor(recordIndex);
        return cursor.Read(recordIndex);
    }

    internal struct Cursor(long[] firstRecordIndices, sbyte[] generations, int position)
    {
        private readonly long[] _firstRecordIndices = firstRecordIndices;
        private readonly sbyte[] _generations = generations;
        private int _position = position;

        /// <summary>
        /// The generation covering <paramref name="recordIndex"/>. Advances rather than assuming the
        /// next run is the right one, so a caller reading a partial batch still lands correctly.
        /// </summary>
        public sbyte Read(long recordIndex)
        {
            if (_generations.Length == 0)
                return 0;

            while (_position + 1 < _firstRecordIndices.Length && _firstRecordIndices[_position + 1] <= recordIndex)
                _position++;

            return _generations[_position];
        }
    }

    /// <summary>
    /// Streams <paramref name="runs"/> into the container as the section body, returning the
    /// checksum so the caller closes the section without a re-read pass.
    /// </summary>
    public static uint Write(Stream stream, List<(long FirstRecordIndex, sbyte Generation)> runs)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(RecordSize * 256);

        try
        {
            int filled = 0;
            foreach ((long firstRecordIndex, sbyte generation) in runs)
            {
                Span<byte> slot = buffer.AsSpan(filled, RecordSize);
                BinaryPrimitives.WriteInt64LittleEndian(slot, firstRecordIndex);
                slot[8] = unchecked((byte)generation);
                slot[9] = 0; slot[10] = 0; slot[11] = 0;
                filled += RecordSize;

                if (filled + RecordSize > buffer.Length)
                {
                    stream.Write(buffer, 0, filled);
                    hasher.Append(buffer.AsSpan(0, filled));
                    filled = 0;
                }
            }

            if (filled > 0)
            {
                stream.Write(buffer, 0, filled);
                hasher.Append(buffer.AsSpan(0, filled));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }
}
