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
    private readonly MemoryMappedViewAccessor _dominatorsAccessor;
    // Nullable: DominatorRetainedBytes was added after DominatorImmediateDominatorAddresses
    // (§10.4 Batch 3) — a cache.bin written by an earlier build has idom data but not this column.
    private readonly MemoryMappedViewAccessor? _retainedBytesAccessor;
    private readonly byte* _dominatorsPtr;
    private readonly byte* _retainedBytesPtr;
    private bool _disposed;

    private DominatorTreeIndexReader(
        DominatorRowIndex rows,
        MemoryMappedViewAccessor dominatorsAccessor,
        MemoryMappedViewAccessor? retainedBytesAccessor)
    {
        _rows = rows;
        _dominatorsAccessor = dominatorsAccessor;
        _retainedBytesAccessor = retainedBytesAccessor;

        byte* p = null;
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

        if (!DominatorRowIndex.TryOpen(container, out DominatorRowIndex? rows) || rows is null)
            return false;

        // Compared against the row-aligned length rather than against the address column's own
        // length: since v6 the address column can be 4 bytes per row while these stay 8.
        long rowAlignedLength = rows.RowAlignedColumnLength;

        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorImmediateDominatorAddresses, out MemoryMappedViewAccessor? dominatorsAccessor, out long dominatorsLength)
            || dominatorsAccessor is null || dominatorsLength != rowAlignedLength)
        {
            rows.Dispose();
            dominatorsAccessor?.Dispose();
            return false;
        }

        MemoryMappedViewAccessor? retainedBytesAccessor = null;
        if (container.TryOpenSectionAccessor(CacheSectionId.DominatorRetainedBytes, out MemoryMappedViewAccessor? candidateAccessor, out long retainedBytesLength)
            && candidateAccessor is not null && retainedBytesLength == rowAlignedLength)
        {
            retainedBytesAccessor = candidateAccessor;
        }
        else
        {
            candidateAccessor?.Dispose();
        }

        reader = new DominatorTreeIndexReader(rows, dominatorsAccessor, retainedBytesAccessor);
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
        return _rows.FindRow(address);
    }

    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rows.Dispose();
        _dominatorsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _dominatorsAccessor.Dispose();

        if (_retainedBytesAccessor is not null)
        {
            _retainedBytesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _retainedBytesAccessor.Dispose();
        }
    }
}
