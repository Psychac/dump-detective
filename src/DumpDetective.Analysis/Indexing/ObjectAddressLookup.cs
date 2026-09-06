using System.IO.MemoryMappedFiles;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Random-access <c>address → (MethodTable, Size)</c> point lookup backed by the disk index — see
/// docs/cache/cache-architecture.md. Unlike <see cref="ObjectIndexReader"/>'s sequential
/// <c>ReadEntries</c>, this holds its mmap accessors open across many calls, since it's meant for
/// repeated point queries over the lifetime of an analysis run, not a single streaming pass.
/// </summary>
/// <remarks>
/// Two-level binary search: first the small in-memory <see cref="SegmentIndexEntry"/> table (segment
/// count, not object count — cheap even as a linear structure, binary search is free insurance), then
/// the matching segment's slice of the mmap'd <c>ObjectAddresses</c> column. Uses plain bounds-checked
/// <see cref="MemoryMappedViewAccessor.ReadUInt64(long)"/> reads rather than the unsafe zero-copy
/// pointer pattern in <see cref="ObjectIndexReader"/>'s <c>ZeroCopyColumnReader</c> — that pattern's
/// complexity is justified for hundreds-of-millions-of-records sequential batch reads, not
/// thousands-of-lookups-per-run point queries.
/// </remarks>
internal sealed class ObjectAddressLookup : IDisposable
{
    private const int ColumnSize = sizeof(ulong);

    private readonly MemoryMappedViewAccessor _addr;
    private readonly MemoryMappedViewAccessor _mt;
    private readonly MemoryMappedViewAccessor _size;
    // TypeId -> MethodTable, loaded once; see CacheSectionId.ObjectTypeDictionary.
    private readonly ulong[] _typeDictionary;
    private readonly int _typeIdWidth;
    // Both derived from the TOC rather than stored; see ObjectColumnSet.
    private readonly int _sizeWidth;
    private readonly ColumnOverflowTable _sizeOverflow;
    // Sorted by Start — segment write order (segment index order) isn't guaranteed to be
    // address-sorted (see docs/cache/cache-architecture.md "why a naive global binary
    // search doesn't work"), so this instance sorts its own copy once at open time.
    private readonly SegmentIndexEntry[] _segmentsByStart;
    private bool _disposed;

    private ObjectAddressLookup(
        MemoryMappedViewAccessor addr, MemoryMappedViewAccessor mt, MemoryMappedViewAccessor size,
        SegmentIndexEntry[] segmentsByStart, ulong[] typeDictionary, int typeIdWidth,
        int sizeWidth, ColumnOverflowTable sizeOverflow)
    {
        _addr = addr;
        _mt = mt;
        _size = size;
        _segmentsByStart = segmentsByStart;
        _typeDictionary = typeDictionary;
        _typeIdWidth = typeIdWidth;
        _sizeWidth = sizeWidth;
        _sizeOverflow = sizeOverflow;
    }

    /// <summary>
    /// Opens a lookup instance over <paramref name="containerPath"/>'s <c>SegmentIndex</c> and
    /// object columns. Returns <c>false</c> — never throws — when the container is missing, has no
    /// <c>SegmentIndex</c> section (old cache, aborted satellite write, or
    /// an aborted satellite write), or is missing any of the three object columns it needs.
    /// Callers own the returned instance and must dispose it.
    /// </summary>
    public static bool TryOpen(string containerPath, out ObjectAddressLookup? lookup)
    {
        lookup = null;
        if (string.IsNullOrWhiteSpace(containerPath)
            || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader)
            || reader is null)
            return false;

        return TryOpen(reader, out lookup);
    }

    /// <summary>
    /// Session-based overload. Preferred wherever the caller already holds the run's container:
    /// this lookup opens <c>SegmentIndex</c> plus three object columns, and on a 14.6M-object dump
    /// those three columns are 334.6 MiB — the entire measured redundancy left after § 6.1, because
    /// a private reader re-verifies what the run's session already verified
    /// (docs/cache/cache-redesign-measurements.md § 7.1).
    /// </summary>
    public static bool TryOpen(CacheContainerReader reader, out ObjectAddressLookup? lookup)
    {
        lookup = null;

        List<SegmentIndexEntry> segments = SegmentIndexWriter.ReadRecords(reader);
        if (segments.Count == 0)
            return false;

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectTypeDictionary, out MemoryMappedViewAccessor? dictAcc, out long dictLen)
            || dictAcc is null || dictLen <= 0 || dictLen % ColumnSize != 0)
        {
            dictAcc?.Dispose();
            return false;
        }

        ulong[] typeDictionary;
        using (dictAcc)
        {
            typeDictionary = new ulong[dictLen / ColumnSize];
            for (int i = 0; i < typeDictionary.Length; i++)
                typeDictionary[i] = dictAcc.ReadUInt64(i * (long)ColumnSize);
        }

        int typeIdWidth = typeDictionary.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(uint);

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? addrAcc, out long addrLen) || addrAcc is null)
            return false;

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectMethodTables, out MemoryMappedViewAccessor? mtAcc, out _) || mtAcc is null)
        {
            addrAcc.Dispose();
            return false;
        }

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectSizes, out MemoryMappedViewAccessor? sizeAcc, out long sizeLen) || sizeAcc is null)
        {
            addrAcc.Dispose();
            mtAcc.Dispose();
            return false;
        }

        long recordCount = addrLen / ColumnSize;
        int sizeWidth = recordCount > 0 && sizeLen % recordCount == 0 ? (int)(sizeLen / recordCount) : 0;
        ColumnOverflowTable sizeOverflow = ColumnOverflowTable.Empty;
        if (!TryResolveSizeEncoding(reader, sizeWidth, ref sizeOverflow))
        {
            addrAcc.Dispose();
            mtAcc.Dispose();
            sizeAcc.Dispose();
            return false;
        }

        SegmentIndexEntry[] segmentsByStart = segments.ToArray();
        Array.Sort(segmentsByStart, static (a, b) => a.Start.CompareTo(b.Start));

        lookup = new ObjectAddressLookup(addrAcc, mtAcc, sizeAcc, segmentsByStart, typeDictionary, typeIdWidth, sizeWidth, sizeOverflow);
        return true;
    }

    /// <summary>
    /// Looks up <paramref name="address"/>. Returns <c>false</c> — an expected outcome, not an
    /// error — when the address falls between segments (LOH/POH gaps, free blocks, padding) or
    /// doesn't land exactly on a record boundary (e.g. an interior pointer; out of scope, see
    /// docs/cache/cache-architecture.md's open questions).
    /// </summary>
    public bool TryGetEntry(ulong address, out ulong methodTable, out ulong size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        methodTable = 0;
        size = 0;

        int segIdx = FindSegment(address);
        if (segIdx < 0)
            return false;

        SegmentIndexEntry segment = _segmentsByStart[segIdx];
        long recordIndex = FindRecord(segment, address);
        if (recordIndex < 0)
            return false;

        int typeId = _typeIdWidth == sizeof(ushort)
            ? _mt.ReadUInt16(recordIndex * sizeof(ushort))
            : (int)_mt.ReadUInt32(recordIndex * sizeof(uint));
        methodTable = (uint)typeId < (uint)_typeDictionary.Length ? _typeDictionary[typeId] : 0;
        size = ReadSize(recordIndex);
        return true;
    }

    private ulong ReadSize(long recordIndex)
    {
        if (_sizeWidth == sizeof(ushort))
        {
            ushort narrow = _size.ReadUInt16(recordIndex * sizeof(ushort));
            return narrow == ushort.MaxValue && _sizeOverflow.TryGetValue(recordIndex, out ulong wide) ? wide : narrow;
        }

        if (_sizeWidth == sizeof(uint))
        {
            uint narrow = _size.ReadUInt32(recordIndex * sizeof(uint));
            return narrow == uint.MaxValue && _sizeOverflow.TryGetValue(recordIndex, out ulong wide) ? wide : narrow;
        }

        return _size.ReadUInt64(recordIndex * ColumnSize);
    }

    /// <summary>
    /// Validates the width recovered from the TOC and, when the column is narrowed, loads its escape
    /// table. A narrowed column whose escape table is missing would report sentinels as real sizes,
    /// so that is a failed open rather than a degraded one.
    /// </summary>
    private static bool TryResolveSizeEncoding(CacheContainerReader reader, int sizeWidth, ref ColumnOverflowTable overflow)
    {
        if (!NarrowColumnWidth.IsSupported(sizeWidth))
            return false;

        if (sizeWidth == NarrowColumnWidth.Full)
            return true;

        if (!ColumnOverflowTable.TryLoad(reader, CacheSectionId.ObjectSizeOverflow, out ColumnOverflowTable? loaded) || loaded is null)
            return false;

        overflow = loaded;
        return true;
    }

    /// <summary>Binary search over the small in-memory segment table for the range containing <paramref name="address"/>.</summary>
    private int FindSegment(ulong address)
    {
        int lo = 0;
        int hi = _segmentsByStart.Length - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            SegmentIndexEntry segment = _segmentsByStart[mid];

            if (address < segment.Start)
                hi = mid - 1;
            else if (address >= segment.End)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

    /// <summary>
    /// Binary search over <paramref name="segment"/>'s record range in the mmap'd
    /// <c>ObjectAddresses</c> column. Relies on within-segment address monotonicity — validated
    /// empirically in docs/cache/cache-architecture.md's Phase 0 (14.6M objects across
    /// Ephemeral/Large segments, zero violations); see that doc for the remaining kinds to validate.
    /// </summary>
    private long FindRecord(SegmentIndexEntry segment, ulong address)
    {
        long lo = segment.FirstRecordIndex;
        long hi = segment.FirstRecordIndex + segment.RecordCount - 1;

        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong candidate = _addr.ReadUInt64(mid * ColumnSize);

            if (address < candidate)
                hi = mid - 1;
            else if (address > candidate)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _addr.Dispose();
        _mt.Dispose();
        _size.Dispose();
    }
}
