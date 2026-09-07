using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// The reverse CSR's row directory, stored as one-byte degrees plus a periodic absolute checkpoint
/// instead of a full <c>int32[R+1]</c> offset column (docs/cache/cache-ideal-design.md §3.2, O3).
/// </summary>
/// <remarks>
/// The offset column is monotone and its successive differences are almost all 0, 1 or 2 — mean
/// in-degree is 2.35 on both reference dumps — so storing it at full width spends 4 bytes a row to
/// hold a number that nearly always fits in one. Degrees at 1 B plus an absolute
/// <c>int32</c> checkpoint every <see cref="CheckpointStride"/> rows is 222.55 MiB → ~59 MiB on the
/// 27.5 GB dump.
///
/// The trade is that <see cref="GetOffset"/> is no longer a single read: it takes the row's
/// checkpoint and sums the degrees before it within the block — at most 63 bytes, one cache line —
/// where the old format read four bytes. At 8,851 lookups per run that is not a cost worth
/// avoiding. <see cref="ReverseEdgeIndexReader.EnumerateChildCounts"/> actually gets *cheaper*: it
/// wanted consecutive differences all along, which is what a degree column already is, so it reads
/// 1 B/row instead of 4 B/row and does no subtraction.
///
/// Deliberately not rebuilt into a resident prefix-sum at open time. That would restore the O(1)
/// read, but it would cost 222 MB of RAM on the 27.5 GB dump to save disk — the priority order
/// (RAM before disk) applied backwards.
///
/// Degrees at or above <see cref="EscapeSentinel"/> store the sentinel and escape to a
/// <see cref="ColumnOverflowTable"/>, the same scheme the object columns use. Hub rows are rare, and
/// a summing pass resolves them with one binary search for the whole block rather than one per
/// escaped row, via <see cref="ColumnOverflowTable.OpenCursor"/>.
/// </remarks>
internal sealed unsafe class ReverseEdgeDegreeColumn : IDisposable
{
    /// <summary>Rows per absolute checkpoint. 64 keeps a block's degree bytes inside one cache line.</summary>
    public const int CheckpointStride = 64;

    /// <summary>A degree of 255 or more is stored as this and resolved from the overflow table.</summary>
    public const byte EscapeSentinel = byte.MaxValue;

    private readonly MemoryMappedViewAccessor? _degreesAccessor;
    private readonly byte* _degrees;
    // Held resident rather than mapped: ceil(R / 64) int32s is 3.47 MiB at the 27.5 GB dump's
    // 58.3M rows, it is touched on every lookup, and it is four orders of magnitude below the
    // memory this format change is part of saving.
    private readonly int[] _checkpoints;
    private readonly ColumnOverflowTable _overflow;
    private readonly long _rowCount;
    private bool _disposed;

    private ReverseEdgeDegreeColumn(
        MemoryMappedViewAccessor? degreesAccessor, int[] checkpoints, ColumnOverflowTable overflow, long rowCount)
    {
        _degreesAccessor = degreesAccessor;
        _checkpoints = checkpoints;
        _overflow = overflow;
        _rowCount = rowCount;

        if (_degreesAccessor is not null)
        {
            byte* p = null;
            _degreesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _degrees = p + _degreesAccessor.PointerOffset;
        }
    }

    public long RowCount => _rowCount;

    public static int CheckpointCountFor(long rowCount) =>
        rowCount <= 0 ? 0 : (int)((rowCount + CheckpointStride - 1) / CheckpointStride);

    /// <summary>
    /// Opens the three sections. Returns <c>false</c> on any inconsistency — a degree column without
    /// its checkpoints or escape table would silently report wrong offsets, so this is all-or-nothing
    /// the way <c>BlockDeltaColumn</c> and the narrowed object columns already are.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, long rowCount, out ReverseEdgeDegreeColumn? column)
    {
        column = null;

        if (rowCount < 0)
            return false;

        if (!container.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeDegrees, out MemoryMappedViewAccessor? degreesAccessor, out long degreesLength)
            || degreesLength != rowCount)
        {
            degreesAccessor?.Dispose();
            return false;
        }

        int expectedCheckpoints = CheckpointCountFor(rowCount);
        if (!container.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeDegreeCheckpoints, out MemoryMappedViewAccessor? checkpointsAccessor, out long checkpointsLength)
            || checkpointsLength != (long)expectedCheckpoints * sizeof(int))
        {
            degreesAccessor?.Dispose();
            checkpointsAccessor?.Dispose();
            return false;
        }

        var checkpoints = new int[expectedCheckpoints];
        using (checkpointsAccessor)
        {
            if (checkpointsAccessor is not null)
                checkpointsAccessor.ReadArray(0, checkpoints, 0, expectedCheckpoints);
        }

        if (!ColumnOverflowTable.TryLoad(container, CacheSectionId.ReverseEdgeDegreeOverflow, out ColumnOverflowTable? overflow) || overflow is null)
        {
            degreesAccessor?.Dispose();
            return false;
        }

        column = new ReverseEdgeDegreeColumn(degreesAccessor, checkpoints, overflow, rowCount);
        return true;
    }

    /// <summary>Row <paramref name="row"/>'s recorded parent count.</summary>
    public int GetDegree(long row)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte stored = _degrees[row];
        if (stored != EscapeSentinel)
            return stored;

        return _overflow.TryGetValue(row, out ulong escaped) ? (int)escaped : 0;
    }

    /// <summary>
    /// Row <paramref name="row"/>'s start index into <c>ReverseEdgeChildren</c>. Accepts
    /// <c>row == RowCount</c> so callers can ask for the exclusive end of the last row.
    /// </summary>
    public int GetOffset(long row)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (row >= _rowCount)
            return TotalEdges();

        long block = row / CheckpointStride;
        long blockStart = block * CheckpointStride;
        int offset = _checkpoints[block];

        if (row == blockStart)
            return offset;

        ColumnOverflowTable.Cursor cursor = _overflow.OpenCursor(blockStart);
        for (long r = blockStart; r < row; r++)
        {
            byte stored = _degrees[r];
            offset += stored == EscapeSentinel ? (int)cursor.Read(r) : stored;
        }

        return offset;
    }

    /// <summary>
    /// Sums the final block to get the exclusive end of the last row — the value the retired
    /// <c>Offsets[RowCount]</c> slot used to hold outright.
    /// </summary>
    private int TotalEdges()
    {
        if (_rowCount == 0)
            return 0;

        long lastBlockStart = ((_rowCount - 1) / CheckpointStride) * CheckpointStride;
        int total = _checkpoints[lastBlockStart / CheckpointStride];

        ColumnOverflowTable.Cursor cursor = _overflow.OpenCursor(lastBlockStart);
        for (long r = lastBlockStart; r < _rowCount; r++)
        {
            byte stored = _degrees[r];
            total += stored == EscapeSentinel ? (int)cursor.Read(r) : stored;
        }

        return total;
    }

    /// <summary>
    /// Sequential degree scan, for callers that want every row's count and no offsets. Resolves
    /// escapes with a single cursor across the whole range instead of a search per escaped row.
    /// </summary>
    public void ForEachDegree(Action<long, int> onRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ColumnOverflowTable.Cursor cursor = _overflow.OpenCursor(0);
        for (long row = 0; row < _rowCount; row++)
        {
            byte stored = _degrees[row];
            int degree = stored == EscapeSentinel ? (int)cursor.Read(row) : stored;
            onRow(row, degree);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_degreesAccessor is not null)
        {
            _degreesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _degreesAccessor.Dispose();
        }
    }
}
