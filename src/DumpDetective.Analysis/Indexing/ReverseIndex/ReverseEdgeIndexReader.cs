using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Analysis.Indexing.Dominator;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Read-only query path over the disk-backed reverse-reference index: "who holds this object?"
/// True CSR since format v8 (docs/cache/cache-format-clean-slate-redesign.md §2) — a direct array
/// slice, <c>Children[Offsets[row]..Offsets[row+1]]</c>, no directory, no per-key header.
/// <see cref="DominatorRowIndex"/> is reused unchanged as the address↔row resolver: both sections
/// key off the exact same reachable-address set the walk that extracts these edges produces, so
/// there is no separate reachable-node numbering to maintain.
/// </summary>
internal sealed unsafe class ReverseEdgeIndexReader : IDisposable
{
    private readonly DominatorRowIndex _rows;
    private readonly ReverseEdgeDegreeColumn _rowDirectory;
    // Nullable: TryOpenSectionAccessor returns a null accessor (not a failure) for a present-but-
    // zero-length section, which is exactly what a dump with genuinely zero recorded edges looks
    // like. Never dereferenced in that case — every row's offsets are equal, so TryGetParents and
    // EnumerateChildCounts never index into it.
    private readonly MemoryMappedViewAccessor? _childrenAccessor;
    private readonly byte* _childrenPtr;
    private bool _disposed;

    private ReverseEdgeIndexReader(
        DominatorRowIndex rows, ReverseEdgeDegreeColumn rowDirectory, MemoryMappedViewAccessor? childrenAccessor)
    {
        _rows = rows;
        _rowDirectory = rowDirectory;
        _childrenAccessor = childrenAccessor;

        if (_childrenAccessor is not null)
        {
            byte* p = null;
            _childrenAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _childrenPtr = p + _childrenAccessor.PointerOffset;
        }
    }

    /// <summary>
    /// Attempts to open the reverse-index sections from <paramref name="container"/>. Returns
    /// <c>false</c> — same as a missing/corrupt satellite section elsewhere in this container — if
    /// the shared row index or either CSR section is absent, or <c>ReverseEdgeOffsets</c>' length
    /// doesn't match the row count; callers fall back to on-demand forward-ref enumeration in that
    /// case. An empty <c>ReverseEdgeChildren</c> is not a failure — a dump with genuinely zero
    /// recorded edges is a legitimate (if unusual) answer, not a corrupt container.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out ReverseEdgeIndexReader? reader)
    {
        reader = null;

        if (!DominatorRowIndex.TryOpen(container, out DominatorRowIndex? rows) || rows is null)
            return false;

        if (!ReverseEdgeDegreeColumn.TryOpen(container, rows.RowCount, out ReverseEdgeDegreeColumn? rowDirectory) || rowDirectory is null)
        {
            rows.Dispose();
            return false;
        }

        // A null accessor here is not a failure: TryOpenSectionAccessor returns true with
        // accessor == null for a present-but-zero-length section, which is exactly what a dump
        // with genuinely zero recorded edges looks like.
        if (!container.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeChildren, out MemoryMappedViewAccessor? childrenAccessor, out _))
        {
            rows.Dispose();
            rowDirectory.Dispose();
            return false;
        }

        reader = new ReverseEdgeIndexReader(rows, rowDirectory, childrenAccessor);
        return true;
    }

    /// <summary>
    /// Retrieves all recorded parent addresses for <paramref name="child"/>. Returns <c>false</c> if
    /// <paramref name="child"/> isn't part of the reachable graph, or is but has no recorded parents
    /// — the same "not present in the index" contract the retired hash-directory format used, kept
    /// exactly rather than exposed as the finer "reachable with zero in-degree" distinction true CSR
    /// happens to make available, since every existing caller already branches on this bool as
    /// "nothing to do here" either way.
    /// <paramref name="truncated"/> is kept for caller-signature compatibility but is always
    /// <c>false</c> — extraction has been uncapped since long before this format existed.
    /// </summary>
    public bool TryGetParents(ulong child, out IReadOnlyList<ulong> parents, out bool truncated)
    {
        parents = Array.Empty<ulong>();
        truncated = false;

        long row = _rows.FindRow(child);
        if (row < 0)
            return false;

        int start = _rowDirectory.GetOffset(row);
        int count = _rowDirectory.GetDegree(row);
        if (count == 0)
            return false;

        var result = new ulong[count];
        for (int i = 0; i < result.Length; i++)
        {
            int parentRow = ReadInt32(_childrenPtr, (start + i) * sizeof(int));
            result[i] = _rows.ReadAddress(parentRow);
        }

        parents = result;
        return true;
    }

    /// <summary>
    /// Invokes <paramref name="onChild"/> once for every reachable row with at least one recorded
    /// parent — zero-degree rows are skipped, matching <see cref="TryGetParents"/>' "no recorded
    /// parents" contract. A single sequential pass over <c>ReverseEdgeOffsets</c>, no row-address
    /// resolution beyond the one call needed to report each qualifying row.
    /// </summary>
    public void EnumerateChildCounts(Action<ulong, int, bool> onChild)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reads the degree column straight through — no offsets, no differencing. The old format
        // had to subtract consecutive offsets to recover exactly this number.
        _rowDirectory.ForEachDegree((row, count) =>
        {
            if (count > 0)
                onChild(_rows.ReadAddress(row), count, false);
        });
    }

    private static int ReadInt32(byte* basePtr, long offset) => Unsafe.ReadUnaligned<int>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rows.Dispose();
        _rowDirectory.Dispose();

        if (_childrenAccessor is not null)
        {
            _childrenAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _childrenAccessor.Dispose();
        }
    }
}
