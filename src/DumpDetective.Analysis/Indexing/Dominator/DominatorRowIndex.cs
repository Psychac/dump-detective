using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// The reachable-node row space and the <c>address ↔ row</c> mapping every dominator and
/// reverse-index reader needs before it can index its own row-aligned column.
/// </summary>
/// <remarks>
/// Since format v10 this is a *derived* index rather than a stored one
/// (docs/cache/cache-ideal-design.md §3.1, R1). It composes two things the container already has:
/// <see cref="MonotonicAddressColumn"/> over <c>ObjectAddresses</c> for addresses, and
/// <see cref="ReachableRowBitmap"/> for membership and rank. The separate sorted
/// <c>DominatorReachableAddresses</c> column it replaces was a second copy of every reachable
/// address — 222.99 MiB with its bases and escape table on the 27.5 GB dump, against 10.70 MiB.
///
/// Row order is unchanged, so every row-aligned column keyed by this index (idom rows, retained
/// bytes, the reverse CSR) keeps its existing meaning: ascending object row is ascending address,
/// because the object column is monotonic by construction (O1).
/// </remarks>
internal sealed class DominatorRowIndex : IDisposable
{
    /// <summary>
    /// Reserved value for a row-index column keyed by this index (currently just
    /// <c>DominatorImmediateDominatorAddresses</c>) meaning "this row's dominator is the virtual
    /// root, not another reachable node." Safe as a sentinel because <see cref="RowCount"/> is
    /// always far below <see cref="uint.MaxValue"/> — the largest measured dump has 58.3M
    /// reachable nodes.
    /// </summary>
    public const uint NoParentRow = uint.MaxValue;

    private readonly MonotonicAddressColumn _objectAddresses;
    private readonly ReachableRowBitmap _reachable;
    private bool _disposed;

    private DominatorRowIndex(MonotonicAddressColumn objectAddresses, ReachableRowBitmap reachable)
    {
        _objectAddresses = objectAddresses;
        _reachable = reachable;
    }

    public long RowCount => _reachable.ReachableRowCount;

    /// <summary>Bytes one row of any <c>ulong</c>-per-row column aligned with this one occupies.</summary>
    public long RowAlignedColumnLength => RowCount * sizeof(ulong);

    /// <summary>
    /// Returns <c>false</c> — the "missing satellite section" outcome every reader already has a
    /// fallback for — when either half is absent. Both are needed: the bitmap alone has no
    /// addresses, and the address column alone does not say which rows the walk reached.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorRowIndex? index)
    {
        index = null;

        if (!MonotonicAddressColumn.TryOpen(container, CacheSectionId.ObjectAddresses,
                CacheSectionId.ObjectAddressBlockBases, CacheSectionId.ObjectAddressOverflow,
                out MonotonicAddressColumn? objectAddresses)
            || objectAddresses is null)
        {
            return false;
        }

        if (!ReachableRowBitmap.TryOpen(container, out ReachableRowBitmap? reachable) || reachable is null)
        {
            objectAddresses.Dispose();
            return false;
        }

        if (reachable.ReachableRowCount <= 0)
        {
            objectAddresses.Dispose();
            reachable.Dispose();
            return false;
        }

        index = new DominatorRowIndex(objectAddresses, reachable);
        return true;
    }

    /// <summary>The address at reachable row <paramref name="row"/>.</summary>
    public ulong ReadAddress(long row)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long objectRow = _reachable.GetObjectRow(row);
        return objectRow < 0 ? 0UL : _objectAddresses.GetAddress(objectRow);
    }

    /// <summary>The reachable row holding <paramref name="address"/>, or -1 if the walk never reached it.</summary>
    public long FindRow(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long objectRow = _objectAddresses.FindRow(address);
        return objectRow < 0 ? -1 : _reachable.GetReachableRow(objectRow);
    }

    /// <summary>
    /// Visits every reachable row in ascending order with its address. For callers that want all
    /// rows — <see cref="ReadAddress"/> per row would be one select each, R of them.
    /// </summary>
    public void ForEachRow(Action<long, ulong> onRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        MonotonicAddressColumn addresses = _objectAddresses;
        _reachable.ForEachReachableRow((reachableRow, objectRow) =>
            onRow(reachableRow, addresses.GetAddress(objectRow)));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _objectAddresses.Dispose();
        _reachable.Dispose();
    }
}
