using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Read-only query path over §D7's persisted dominator-tree sections — binary search over the
/// sorted <c>DominatorReachableAddresses</c> column, same idiom as
/// <see cref="ForwardIndex.ForwardEdgeIndexReader"/>/<see cref="ReverseIndex.ReverseEdgeIndexReader"/>
/// but simpler (flat aligned columns, no bucket/directory indirection needed since this section
/// isn't built from a hash-partitioned parallel scan).
/// </summary>
internal sealed unsafe class DominatorTreeIndexReader : IDisposable
{
    private readonly MemoryMappedViewAccessor _addressesAccessor;
    private readonly MemoryMappedViewAccessor _dominatorsAccessor;
    private readonly byte* _addressesPtr;
    private readonly byte* _dominatorsPtr;
    private readonly long _count;
    private bool _disposed;

    private DominatorTreeIndexReader(
        MemoryMappedViewAccessor addressesAccessor,
        MemoryMappedViewAccessor dominatorsAccessor,
        long count)
    {
        _addressesAccessor = addressesAccessor;
        _dominatorsAccessor = dominatorsAccessor;
        _count = count;

        byte* p = null;
        _addressesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _addressesPtr = p + _addressesAccessor.PointerOffset;

        p = null;
        _dominatorsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _dominatorsPtr = p + _dominatorsAccessor.PointerOffset;
    }

    /// <summary>
    /// Attempts to open the persisted dominator-tree sections. Returns <c>false</c> — same as any
    /// missing/corrupt satellite section — if either section is absent or empty; callers fall back
    /// to recomputing the tree in that case.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorTreeIndexReader? reader)
    {
        reader = null;

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorReachableAddresses, out MemoryMappedViewAccessor? addressesAccessor, out long addressesLength)
            || addressesAccessor is null || addressesLength == 0)
        {
            return false;
        }

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorImmediateDominatorAddresses, out MemoryMappedViewAccessor? dominatorsAccessor, out long dominatorsLength)
            || dominatorsAccessor is null || dominatorsLength != addressesLength)
        {
            addressesAccessor.Dispose();
            return false;
        }

        reader = new DominatorTreeIndexReader(addressesAccessor, dominatorsAccessor, addressesLength / sizeof(ulong));
        return true;
    }

    /// <summary>
    /// Retrieves the immediate-dominator address for <paramref name="address"/>. Returns
    /// <c>false</c> if <paramref name="address"/> wasn't part of the reachable graph when this
    /// section was written (not an error — could be a stale/different snapshot, or an address this
    /// tree never reached).
    /// </summary>
    public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress)
    {
        dominatorAddress = 0;

        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong midAddress = ReadUInt64(_addressesPtr, mid * sizeof(ulong));

            if (midAddress == address)
            {
                dominatorAddress = ReadUInt64(_dominatorsPtr, mid * sizeof(ulong));
                return true;
            }

            if (midAddress < address)
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
        _dominatorsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _addressesAccessor.Dispose();
        _dominatorsAccessor.Dispose();
    }
}
