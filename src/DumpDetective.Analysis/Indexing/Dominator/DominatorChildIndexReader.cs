using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Read-only query path over the dominator child index (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — "what would freeing this
/// object free, one level down." Deliberately minimal: this exists to round-trip-test the on-disk
/// format the writer produces. The richer query surface (<c>EnumerateRetainedSet</c>,
/// <c>TryGetRetainedBytes</c>'s subtree-sum walk, the <c>IDominatorTreeProvider</c> facade) is
/// Batch 3 (§10.6), not this class.
/// </summary>
internal sealed unsafe class DominatorChildIndexReader : IDisposable
{
    private readonly MemoryMappedViewAccessor _addressesAccessor;
    private readonly MemoryMappedViewAccessor _childOffsetsAccessor;
    // Nullable: legitimately absent (zero-length, not missing/corrupt) whenever nothing in the
    // whole graph has any dominator-tree children at all — e.g. every reachable node is a direct
    // GC root with no children of its own.
    private readonly MemoryMappedViewAccessor? _childAddressesAccessor;
    private readonly byte* _addressesPtr;
    private readonly byte* _childOffsetsPtr;
    private readonly byte* _childAddressesPtr;
    private readonly long _rowCount;
    private bool _disposed;

    private DominatorChildIndexReader(
        MemoryMappedViewAccessor addressesAccessor,
        MemoryMappedViewAccessor childOffsetsAccessor,
        MemoryMappedViewAccessor? childAddressesAccessor,
        long rowCount)
    {
        _addressesAccessor = addressesAccessor;
        _childOffsetsAccessor = childOffsetsAccessor;
        _childAddressesAccessor = childAddressesAccessor;
        _rowCount = rowCount;

        byte* p = null;
        _addressesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _addressesPtr = p + _addressesAccessor.PointerOffset;

        p = null;
        _childOffsetsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _childOffsetsPtr = p + _childOffsetsAccessor.PointerOffset;

        if (_childAddressesAccessor is not null)
        {
            p = null;
            _childAddressesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _childAddressesPtr = p + _childAddressesAccessor.PointerOffset;
        }
    }

    /// <summary>
    /// Attempts to open the child-index sections. Returns <c>false</c> — same as any missing/corrupt
    /// satellite section — if <c>DominatorReachableAddresses</c>/<c>DominatorChildOffsets</c> are
    /// absent or the offsets column's length doesn't match the row count + 1.
    /// <c>DominatorChildAddresses</c> being empty is not treated as a failure — see the field comment.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorChildIndexReader? reader)
    {
        reader = null;

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorReachableAddresses, out MemoryMappedViewAccessor? addressesAccessor, out long addressesLength)
            || addressesAccessor is null || addressesLength == 0)
        {
            return false;
        }

        long rowCount = addressesLength / sizeof(ulong);

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorChildOffsets, out MemoryMappedViewAccessor? childOffsetsAccessor, out long childOffsetsLength)
            || childOffsetsAccessor is null || childOffsetsLength != (rowCount + 1) * sizeof(int))
        {
            addressesAccessor.Dispose();
            childOffsetsAccessor?.Dispose();
            return false;
        }

        // A missing DominatorChildAddresses accessor here is only ever the legitimate
        // zero-total-children case (see the field comment) — TryOpenSectionAccessor already
        // distinguishes that from a genuinely absent/corrupt section (it would have returned false).
        container.TryOpenSectionAccessor(CacheSectionId.DominatorChildAddresses, out MemoryMappedViewAccessor? childAddressesAccessor, out _);

        reader = new DominatorChildIndexReader(addressesAccessor, childOffsetsAccessor, childAddressesAccessor, rowCount);
        return true;
    }

    /// <summary>
    /// Retrieves <paramref name="address"/>'s dominator-tree children. Returns <c>false</c> if the
    /// address wasn't part of the reachable graph when this section was written; an empty
    /// <paramref name="children"/> array (with a <c>true</c> return) is a real answer — the object
    /// simply doesn't dominate anything.
    /// </summary>
    public bool TryGetChildren(ulong address, out ulong[] children)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        children = Array.Empty<ulong>();

        long row = FindRow(address);
        if (row < 0)
            return false;

        int start = ReadInt32(_childOffsetsPtr, row * sizeof(int));
        int end = ReadInt32(_childOffsetsPtr, (row + 1) * sizeof(int));
        if (end == start)
            return true;

        children = new ulong[end - start];
        for (int i = 0; i < children.Length; i++)
            children[i] = ReadUInt64(_childAddressesPtr, (start + i) * (long)sizeof(ulong));

        return true;
    }

    private long FindRow(ulong address)
    {
        long lo = 0, hi = _rowCount - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong candidate = ReadUInt64(_addressesPtr, mid * sizeof(ulong));

            if (candidate == address)
                return mid;

            if (candidate < address)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    private static int ReadInt32(byte* basePtr, long offset) => Unsafe.ReadUnaligned<int>(basePtr + offset);
    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _addressesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _childOffsetsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _addressesAccessor.Dispose();
        _childOffsetsAccessor.Dispose();

        if (_childAddressesAccessor is not null)
        {
            _childAddressesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _childAddressesAccessor.Dispose();
        }
    }
}
