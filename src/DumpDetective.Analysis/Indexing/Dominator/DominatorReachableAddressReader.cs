using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Read-only query path over the <c>DominatorReachableAddresses</c> section
/// <see cref="DominatorReachableAddressWriter"/> writes: "is this object reachable from a GC
/// root?" — answerable from disk without re-running the walk. Same
/// binary-search-over-a-sorted-mmap'd-column idiom as <see cref="DominatorTreeIndexReader"/>, but
/// over a single column (no dominator-address pairing — Stage A has none to pair with).
/// Implements <see cref="IReachableAddressProvider"/> directly — its one method already matches
/// this reader's own signature, so a separate adapter class would add nothing.
/// </summary>
internal sealed unsafe class DominatorReachableAddressReader : IReachableAddressProvider, IDisposable
{
    private readonly MemoryMappedViewAccessor _addressesAccessor;
    private readonly byte* _addressesPtr;
    private readonly long _count;
    private bool _disposed;

    private DominatorReachableAddressReader(MemoryMappedViewAccessor addressesAccessor, long count)
    {
        _addressesAccessor = addressesAccessor;
        _count = count;

        byte* p = null;
        _addressesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _addressesPtr = p + _addressesAccessor.PointerOffset;
    }

    /// <summary>
    /// Attempts to open the persisted reachable-address section. Returns <c>false</c> — same as
    /// any missing/corrupt satellite section elsewhere in this container — if the section is
    /// absent or empty; callers fall back to whatever they did before this section existed.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorReachableAddressReader? reader)
    {
        reader = null;

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorReachableAddresses, out MemoryMappedViewAccessor? addressesAccessor, out long length)
            || addressesAccessor is null || length == 0)
        {
            return false;
        }

        reader = new DominatorReachableAddressReader(addressesAccessor, length / sizeof(ulong));
        return true;
    }

    /// <summary>
    /// Binary search over the sorted address column. <c>false</c> means either the object isn't
    /// reachable from any GC root, or it's not a live heap object at all — the section carries no
    /// separate signal for the two.
    /// </summary>
    public bool IsReachable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong candidate = ReadUInt64(_addressesPtr, mid * sizeof(ulong));

            if (candidate == address)
                return true;

            if (candidate < address)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return false;
    }

    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _addressesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _addressesAccessor.Dispose();
    }
}
