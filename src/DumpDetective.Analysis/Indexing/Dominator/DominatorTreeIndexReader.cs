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
    private readonly DominatorRowIndex _rows;
    private readonly MemoryMappedViewAccessor _dominatorRowsAccessor;
    // Nullable: DominatorRetainedBytes was added after DominatorImmediateDominatorAddresses
    // (§10.4 Batch 3) — a cache.bin written by an earlier build has idom data but not this column.
    private readonly MemoryMappedViewAccessor? _retainedBytesAccessor;
    private readonly byte* _dominatorRowsPtr;
    private readonly byte* _retainedBytesPtr;
    private bool _disposed;

    private DominatorTreeIndexReader(
        DominatorRowIndex rows,
        MemoryMappedViewAccessor dominatorRowsAccessor,
        MemoryMappedViewAccessor? retainedBytesAccessor)
    {
        _rows = rows;
        _dominatorRowsAccessor = dominatorRowsAccessor;
        _retainedBytesAccessor = retainedBytesAccessor;

        byte* p = null;
        _dominatorRowsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _dominatorRowsPtr = p + _dominatorRowsAccessor.PointerOffset;

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

        if (!DominatorRowIndex.TryOpen(container, out DominatorRowIndex? rows) || rows is null)
            return false;

        // DominatorImmediateDominatorAddresses is a fixed 4 bytes/row since format v7 — no width
        // variability to detect the way the narrowed object columns have, because a v6 container
        // (8 bytes/row, address-keyed) never reaches this reader at all: CacheFileHeader.TryRead
        // already rejects any format version other than the current one before any section is opened.
        long expectedLength = rows.RowCount * sizeof(uint);

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorImmediateDominatorAddresses, out MemoryMappedViewAccessor? dominatorRowsAccessor, out long dominatorRowsLength)
            || dominatorRowsAccessor is null || dominatorRowsLength != expectedLength)
        {
            rows.Dispose();
            dominatorRowsAccessor?.Dispose();
            return false;
        }

        MemoryMappedViewAccessor? retainedBytesAccessor = null;
        if (container.TryOpenSectionAccessor(CacheSectionId.DominatorRetainedBytes, out MemoryMappedViewAccessor? candidateAccessor, out long retainedBytesLength)
            && candidateAccessor is not null && retainedBytesLength == rows.RowAlignedColumnLength)
        {
            retainedBytesAccessor = candidateAccessor;
        }
        else
        {
            candidateAccessor?.Dispose();
        }

        reader = new DominatorTreeIndexReader(rows, dominatorRowsAccessor, retainedBytesAccessor);
        return true;
    }

    /// <summary>
    /// Retrieves the immediate-dominator address for <paramref name="address"/>. Returns
    /// <c>false</c> if <paramref name="address"/> wasn't part of the reachable graph when this
    /// section was written (not an error — could be a stale/different snapshot, or an address this
    /// tree never reached). <paramref name="dominatorAddress"/> is <c>0</c> — not a failure — when
    /// <paramref name="address"/> is a direct child of the virtual root (a real GC root object).
    /// </summary>
    public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress)
    {
        dominatorAddress = 0;

        long row = FindRow(address);
        if (row < 0)
            return false;

        uint dominatorRow = ReadUInt32(_dominatorRowsPtr, row * sizeof(uint));
        dominatorAddress = dominatorRow == DominatorRowIndex.NoParentRow ? 0UL : _rows.ReadAddress(dominatorRow);
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
        return _rows.FindRow(address);
    }

    private static uint ReadUInt32(byte* basePtr, long offset) => Unsafe.ReadUnaligned<uint>(basePtr + offset);
    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rows.Dispose();
        _dominatorRowsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _dominatorRowsAccessor.Dispose();

        if (_retainedBytesAccessor is not null)
        {
            _retainedBytesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _retainedBytesAccessor.Dispose();
        }
    }
}
