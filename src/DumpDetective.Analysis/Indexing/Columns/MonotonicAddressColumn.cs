using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Columns;

/// <summary>
/// A strictly ascending address column plus the two-level <c>address → row</c> rank search over it:
/// a binary search of the resident block bases, then a search inside that one block of the mapped
/// delta column (docs/cache/cache-ideal-design.md §3.1, R1).
/// </summary>
/// <remarks>
/// One type for what were three separate mechanisms. <c>ObjectAddresses</c> and
/// <c>DominatorReachableAddresses</c> are the same shape — block-delta encoded, ascending, row
/// indexed — and both were searched by hand-rolled code that also had to know the encoding.
///
/// Why this replaces a hash table rather than merely tidying one: measured against the real
/// 27.5 GB column, a <c>Dictionary&lt;ulong,int&gt;</c> over the same 87.1M addresses is
/// **2,325.9 MB resident** where this is **0.65 MiB** of block bases plus mapped pages — a 3,584×
/// ratio. Per probe it is *faster* than the dictionary in ascending order (63.9 ns vs 68.8 ns) and
/// 1.60× slower in real reference-locality order, because the dictionary degrades as its table
/// outgrows cache while the bases stay in L2 regardless of dump size. Across the 137M-edge walk
/// that is +3.4 s for −2.3 GB (§7.2).
///
/// The search decodes rather than comparing stored deltas. An escaped record stores an all-ones
/// sentinel, which would compare larger than every real delta and break the search even though the
/// *decoded* column is monotonic.
///
/// Monotonicity is a construction guarantee, not an observation: the writer sorts segments by
/// <c>Start</c> (O1) and <c>SegmentAddressContiguityDiscrepancyTests</c> pins the two invariants it
/// rests on — each segment ascends internally, and segments are disjoint.
/// </remarks>
internal sealed unsafe class MonotonicAddressColumn : IDisposable
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _column;
    private readonly BlockDeltaColumn? _deltas;
    private readonly int _width;
    private bool _disposed;

    public long RowCount { get; }

    private MonotonicAddressColumn(MemoryMappedViewAccessor accessor, long rowCount, int width, BlockDeltaColumn? deltas)
    {
        _accessor = accessor;
        RowCount = rowCount;
        _width = width;
        _deltas = deltas;

        byte* p = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _column = p + _accessor.PointerOffset;
    }

    /// <summary>
    /// Opens the column and, when it is delta encoded, its block bases and escape table. Returns
    /// <c>false</c> — the "missing satellite section" outcome every caller already handles — if any
    /// piece is absent or inconsistent. All-or-nothing: a delta column without its bases decodes to
    /// addresses that match nothing, which reads as an empty heap rather than as a broken cache.
    /// </summary>
    public static bool TryOpen(
        CacheContainerReader container,
        CacheSectionId columnId,
        CacheSectionId blockBasesId,
        CacheSectionId overflowId,
        out MonotonicAddressColumn? column)
    {
        column = null;

        if (!container.TryOpenSectionAccessor(columnId, out MemoryMappedViewAccessor? accessor, out long length)
            || accessor is null || length == 0)
        {
            accessor?.Dispose();
            return false;
        }

        if (!container.TryGetSectionInfo(columnId, out CacheTocEntry entry) || entry.RecordCount <= 0)
        {
            accessor.Dispose();
            return false;
        }

        long rowCount = entry.RecordCount;
        int width = ObjectColumnSet.WidthOf(length, rowCount);

        BlockDeltaColumn? deltas = null;
        if (width == BlockDeltaColumn.DeltaWidth)
        {
            if (!BlockDeltaColumn.TryLoad(container, blockBasesId, overflowId, rowCount, out deltas))
            {
                accessor.Dispose();
                return false;
            }
        }
        else if (width != sizeof(ulong))
        {
            accessor.Dispose();
            return false;
        }

        column = new MonotonicAddressColumn(accessor, rowCount, width, deltas);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetAddress(long row) =>
        _deltas is null
            ? Unsafe.ReadUnaligned<ulong>(_column + row * sizeof(ulong))
            : _deltas.Decode(Unsafe.ReadUnaligned<uint>(_column + row * BlockDeltaColumn.DeltaWidth), row);

    /// <summary>The row holding <paramref name="address"/>, or -1 when it is not in this column.</summary>
    public long FindRow(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Narrow to one block off the resident bases first when the column is delta encoded. That
        // is the whole point: the hot part of the search touches 0.65 MiB that stays in cache, and
        // only the final in-block probe reaches a mapped page.
        long lo = 0;
        long hi = RowCount - 1;

        if (_deltas is not null && _deltas.TryFindBlock(address, RowCount, out long blockStart, out long blockEnd))
        {
            lo = blockStart;
            hi = blockEnd;
        }

        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            ulong candidate = GetAddress(mid);

            if (candidate == address)
                return mid;

            if (candidate < address)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    /// <summary>Bytes one row of any <c>ulong</c>-per-row column aligned with this one occupies.</summary>
    public long RowAlignedColumnLength => RowCount * sizeof(ulong);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
    }
}
