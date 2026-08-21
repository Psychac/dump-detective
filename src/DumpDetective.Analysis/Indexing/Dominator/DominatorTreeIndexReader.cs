using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Read-only query path over the persisted per-node scalar dominator-tree columns (§10.4,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — binary search over the
/// sorted <c>DominatorReachableAddresses</c> column, same idiom as
/// <see cref="ForwardIndex.ForwardEdgeIndexReader"/>/<see cref="ReverseIndex.ReverseEdgeIndexReader"/>
/// but simpler (flat aligned columns, no bucket/directory indirection needed since these sections
/// aren't built from a hash-partitioned parallel scan).
/// </summary>
internal sealed unsafe class DominatorTreeIndexReader : IDisposable
{
    private readonly MemoryMappedViewAccessor _addressesAccessor;
    private readonly MemoryMappedViewAccessor _dominatorsAccessor;
    // Nullable: DominatorRetainedBytes was added after DominatorImmediateDominatorAddresses
    // (§10.4 Batch 3) — a cache.bin written by an earlier build has idom data but not this column.
    private readonly MemoryMappedViewAccessor? _retainedBytesAccessor;
    private readonly byte* _addressesPtr;
    private readonly byte* _dominatorsPtr;
    private readonly byte* _retainedBytesPtr;
    private readonly long _count;
    private bool _disposed;

    private DominatorTreeIndexReader(
        MemoryMappedViewAccessor addressesAccessor,
        MemoryMappedViewAccessor dominatorsAccessor,
        MemoryMappedViewAccessor? retainedBytesAccessor,
        long count)
    {
        _addressesAccessor = addressesAccessor;
        _dominatorsAccessor = dominatorsAccessor;
        _retainedBytesAccessor = retainedBytesAccessor;
        _count = count;

        byte* p = null;
        _addressesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _addressesPtr = p + _addressesAccessor.PointerOffset;

        p = null;
        _dominatorsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _dominatorsPtr = p + _dominatorsAccessor.PointerOffset;

        if (_retainedBytesAccessor is not null)
        {
            p = null;
            _retainedBytesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _retainedBytesPtr = p + _retainedBytesAccessor.PointerOffset;
        }
    }

    /// <summary>
    /// Attempts to open the persisted dominator-tree sections. Returns <c>false</c> — same as any
    /// missing/corrupt satellite section — if the addresses/dominators sections are absent, empty,
    /// or mismatched in length. <c>DominatorRetainedBytes</c> being absent is not a failure — see
    /// the field comment — it just means <see cref="TryGetRetainedBytes"/> always returns <c>false</c>.
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

        MemoryMappedViewAccessor? retainedBytesAccessor = null;
        if (container.TryOpenSectionAccessor(CacheSectionId.DominatorRetainedBytes, out MemoryMappedViewAccessor? candidateAccessor, out long retainedBytesLength)
            && candidateAccessor is not null && retainedBytesLength == addressesLength)
        {
            retainedBytesAccessor = candidateAccessor;
        }
        else
        {
            candidateAccessor?.Dispose();
        }

        reader = new DominatorTreeIndexReader(addressesAccessor, dominatorsAccessor, retainedBytesAccessor, addressesLength / sizeof(ulong));
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

        long row = FindRow(address);
        if (row < 0)
            return false;

        dominatorAddress = ReadUInt64(_dominatorsPtr, row * sizeof(ulong));
        return true;
    }

    /// <summary>
    /// Retrieves the exact retained bytes for <paramref name="address"/> (subtree sum including its
    /// own shallow size). Returns <c>false</c> if the address wasn't reachable, or if this
    /// cache.bin predates the <c>DominatorRetainedBytes</c> section (§10.4 Batch 3).
    /// </summary>
    public bool TryGetRetainedBytes(ulong address, out ulong retainedBytes)
    {
        retainedBytes = 0;

        if (_retainedBytesAccessor is null)
            return false;

        long row = FindRow(address);
        if (row < 0)
            return false;

        retainedBytes = ReadUInt64(_retainedBytesPtr, row * sizeof(ulong));
        return true;
    }

    private long FindRow(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong midAddress = ReadUInt64(_addressesPtr, mid * sizeof(ulong));

            if (midAddress == address)
                return mid;

            if (midAddress < address)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
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

        if (_retainedBytesAccessor is not null)
        {
            _retainedBytesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _retainedBytesAccessor.Dispose();
        }
    }
}
