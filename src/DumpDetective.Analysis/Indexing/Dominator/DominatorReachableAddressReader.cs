using DumpDetective.Platform.Storage.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Read-only query path over the <c>DominatorReachableAddresses</c> section that
/// <see cref="DominatorReachableAddressWriter"/> writes: "is this object reachable from a GC
/// root?" — answerable from disk without re-running the walk. Implements
/// <see cref="IReachableAddressProvider"/> directly, since the one method it needs already matches
/// <see cref="DominatorRowIndex"/>'s own search.
/// </summary>
internal sealed class DominatorReachableAddressReader : IReachableAddressProvider, IDisposable
{
    private readonly DominatorRowIndex _rows;
    private bool _disposed;

    private DominatorReachableAddressReader(DominatorRowIndex rows)
    {
        _rows = rows;
    }

    /// <summary>
    /// Attempts to open the persisted reachable-address section. Returns <c>false</c> — the same as
    /// any missing/corrupt satellite section elsewhere in the container — if the section is absent
    /// or empty; callers fall back to what they did before the section existed.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorReachableAddressReader? reader)
    {
        reader = null;

        if (!DominatorRowIndex.TryOpen(container, out DominatorRowIndex? rows) || rows is null)
            return false;

        reader = new DominatorReachableAddressReader(rows);
        return true;
    }

    /// <summary>
    /// Returns <c>false</c> when the walk that produced this section never reached
    /// <paramref name="address"/> — not an error, just an unreachable (or unknown) object.
    /// </summary>
    public bool IsReachable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _rows.FindRow(address) >= 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rows.Dispose();
    }
}
