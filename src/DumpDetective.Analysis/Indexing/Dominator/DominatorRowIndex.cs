using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// The sorted <c>DominatorReachableAddresses</c> column and the <c>address → row</c> binary search
/// over it, which every dominator reader needs before it can index its own row-aligned column.
/// </summary>
/// <remarks>
/// Extracted when format v6 made the column block-delta encoded
/// (docs/cache/cache-format-clean-slate-redesign.md §10.4): the three readers that each kept their
/// own copy of this search would otherwise each need their own copy of the decode too, and a row
/// count that no longer follows from the section's length.
/// </remarks>
internal sealed unsafe class DominatorRowIndex : IDisposable
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _addressesPtr;
    private readonly BlockDeltaColumn? _deltas;
    private bool _disposed;

    public long RowCount { get; }

    private DominatorRowIndex(MemoryMappedViewAccessor accessor, long rowCount, BlockDeltaColumn? deltas)
    {
        _accessor = accessor;
        RowCount = rowCount;
        _deltas = deltas;

        byte* p = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _addressesPtr = p + _accessor.PointerOffset;
    }

    /// <summary>
    /// Returns <c>false</c> — the same "treat it as a missing satellite section" outcome every
    /// dominator reader already has a fallback for — when the column is absent, empty, or delta
    /// encoded without the block bases it needs to decode.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorRowIndex? index)
    {
        index = null;

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorReachableAddresses, out MemoryMappedViewAccessor? accessor, out long length)
            || accessor is null || length == 0)
        {
            return false;
        }

        if (!container.TryGetSectionInfo(CacheSectionId.DominatorReachableAddresses, out CacheTocEntry entry)
            || entry.RecordCount <= 0)
        {
            accessor.Dispose();
            return false;
        }

        long rowCount = entry.RecordCount;
        int width = ObjectColumnSet.WidthOf(length, rowCount);

        BlockDeltaColumn? deltas = null;
        if (width == BlockDeltaColumn.DeltaWidth)
        {
            if (!BlockDeltaColumn.TryLoad(container, CacheSectionId.DominatorReachableBlockBases,
                    CacheSectionId.DominatorReachableOverflow, rowCount, out deltas))
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

        index = new DominatorRowIndex(accessor, rowCount, deltas);
        return true;
    }

    /// <summary>Bytes one row of any <c>ulong</c>-per-row column aligned with this one occupies.</summary>
    public long RowAlignedColumnLength => RowCount * sizeof(ulong);

    public ulong ReadAddress(long row) =>
        _deltas is null
            ? Unsafe.ReadUnaligned<ulong>(_addressesPtr + row * sizeof(ulong))
            : _deltas.Decode(Unsafe.ReadUnaligned<uint>(_addressesPtr + row * BlockDeltaColumn.DeltaWidth), row);

    /// <summary>The row holding <paramref name="address"/>, or -1 if this tree never reached it.</summary>
    public long FindRow(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long lo = 0, hi = RowCount - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong candidate = ReadAddress(mid);

            if (candidate == address)
                return mid;

            if (candidate < address)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
    }
}
