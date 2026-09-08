using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Answers "what would freeing this object free, one level down" — the dominator-tree child
/// direction — without a persisted child-list section. Format v7 (the aggressive option in
/// docs/cache/cache-format-clean-slate-redesign.md §4) derives it on demand by inverting the
/// persisted <c>DominatorImmediateDominatorAddresses</c> row-index column once, in memory, the first
/// time a query actually needs it — not on every build, and not even on every run: measurements §15
/// found this direction's only production consumer, <c>StaticRootLeakDetector</c>, called zero times
/// across every real dump tested.
/// </summary>
internal sealed unsafe class DominatorChildIndexReader : IDisposable
{
    private readonly DominatorRowIndex _rows;
    private readonly MemoryMappedViewAccessor _dominatorRowsAccessor;
    private readonly byte* _dominatorRowsPtr;
    private readonly object _buildGate = new();
    // Both null until EnsureChildIndexBuilt's first call; published together (offsets last) so a
    // racing reader either sees neither or both fully populated, never a half-built pair.
    private int[]? _childRowsByRow;
    private int[]? _childOffsetsByRow;
    private bool _disposed;

    private DominatorChildIndexReader(DominatorRowIndex rows, MemoryMappedViewAccessor dominatorRowsAccessor)
    {
        _rows = rows;
        _dominatorRowsAccessor = dominatorRowsAccessor;

        byte* p = null;
        _dominatorRowsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _dominatorRowsPtr = p + _dominatorRowsAccessor.PointerOffset;
    }

    /// <summary>
    /// Attempts to open the sections the inversion needs. Returns <c>false</c> — same as any
    /// missing/corrupt satellite section — if <c>DominatorReachableAddresses</c>/
    /// <c>DominatorImmediateDominatorAddresses</c> are absent or the latter's length doesn't match
    /// the row count. Does no inversion work itself — see <see cref="EnsureChildIndexBuilt"/>.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorChildIndexReader? reader)
    {
        reader = null;

        if (!DominatorRowIndex.TryOpen(container, out DominatorRowIndex? rows) || rows is null)
            return false;

        long expectedLength = rows.RowCount * sizeof(uint);
        if (!container.TryOpenSectionAccessor(CacheSectionId.DominatorImmediateDominatorAddresses, out MemoryMappedViewAccessor? dominatorRowsAccessor, out long dominatorRowsLength)
            || dominatorRowsAccessor is null || dominatorRowsLength != expectedLength)
        {
            rows.Dispose();
            dominatorRowsAccessor?.Dispose();
            return false;
        }

        reader = new DominatorChildIndexReader(rows, dominatorRowsAccessor);
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

        long row = _rows.FindRow(address);
        if (row < 0)
            return false;

        EnsureChildIndexBuilt();

        int start = _childOffsetsByRow![(int)row];
        int end = _childOffsetsByRow[(int)row + 1];
        if (end == start)
            return true;

        children = new ulong[end - start];
        for (int i = 0; i < children.Length; i++)
            children[i] = _rows.ReadAddress(_childRowsByRow![start + i]);

        return true;
    }

    /// <summary>
    /// Builds the inverted child-row CSR from the persisted idom column, once, on first use. Same
    /// counting-sort-into-CSR shape the old write-time <c>DominatorChildIndexBuilder</c> used, just
    /// with one source (idom rows) instead of two merged sources (real dominator-tree edges + folded
    /// leaves) — the persisted idom row already carries both, which is exactly the precondition
    /// verified in docs/cache/cache-format-clean-slate-redesign.md §4 before this option was chosen.
    /// </summary>
    private void EnsureChildIndexBuilt()
    {
        if (Volatile.Read(ref _childOffsetsByRow) is not null)
            return;

        lock (_buildGate)
        {
            if (_childOffsetsByRow is not null)
                return;

            int rowCount = (int)_rows.RowCount;
            var offsets = new int[rowCount + 1];

            for (int row = 0; row < rowCount; row++)
            {
                uint parentRow = ReadDominatorRow(row);
                if (parentRow != DominatorRowIndex.NoParentRow)
                    offsets[parentRow + 1]++;
            }

            for (int i = 0; i < rowCount; i++)
                offsets[i + 1] += offsets[i];

            var cursor = (int[])offsets.Clone();
            var childRows = new int[offsets[rowCount]];

            for (int row = 0; row < rowCount; row++)
            {
                uint parentRow = ReadDominatorRow(row);
                if (parentRow != DominatorRowIndex.NoParentRow)
                    childRows[cursor[parentRow]++] = row;
            }

            _childRowsByRow = childRows;
            Volatile.Write(ref _childOffsetsByRow, offsets);
        }
    }

    private uint ReadDominatorRow(long row) => Unsafe.ReadUnaligned<uint>(_dominatorRowsPtr + row * sizeof(uint));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rows.Dispose();
        _dominatorRowsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _dominatorRowsAccessor.Dispose();
    }
}
