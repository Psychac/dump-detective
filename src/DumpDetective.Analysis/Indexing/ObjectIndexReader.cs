using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal sealed class ObjectIndexReader : IObjectIndexReader
{
    /// <summary>Shared singleton — stateless, safe to reuse across calls.</summary>
    internal static readonly ObjectIndexReader Instance = new();

    private const int ColumnSize = sizeof(ulong);
    private const int GenColumnSize = sizeof(sbyte);

    // Records materialized per batch before yielding. Pointers can't be used directly inside
    // this iterator (the C# compiler forbids unsafe/pointer syntax anywhere lexically inside a
    // method containing yield), so the pointer-based read is done in ZeroCopyColumnReader, a
    // plain (non-iterator) class, and this method just yields out of the filled batch.
    private const int BatchRecords = 65536;

    public IEnumerable<HeapEntry> ReadEntries(string containerPath)
    {
        return ReadDiskEntries(containerPath);
    }

    public IEnumerable<HeapEntry> ReadEntriesRange(string containerPath, long startRecord, long recordCount)
    {
        return ReadDiskEntriesRange(containerPath, startRecord, recordCount);
    }

    public bool TryGetEntry(string containerPath, ulong address, out ulong methodTable, out ulong size)
    {
        methodTable = 0;
        size = 0;

        if (!ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup) || lookup is null)
            return false;

        using (lookup)
        {
            return lookup.TryGetEntry(address, out methodTable, out size);
        }
    }

    // Internal static helper kept for call sites that don't need DI. Opens a throwaway session;
    // prefer the CacheContainerReader overload when enumerating more than once per run, so the
    // columns' checksums are verified once rather than per enumeration.
    internal static IEnumerable<HeapEntry> ReadDiskEntries(string containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath)
            || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader)
            || reader is null)
            yield break;

        foreach (HeapEntry entry in ReadDiskEntries(reader))
            yield return entry;
    }

    /// <summary>
    /// Streams every record using an already-open session, so the four object columns are
    /// checksum-verified once for that session's lifetime instead of once per call — see
    /// <see cref="CacheContainerReader"/> and docs/cache/cache-redesign-measurements.md § 5.
    /// The session is owned by the caller and is not disposed here.
    /// </summary>
    internal static IEnumerable<HeapEntry> ReadDiskEntries(CacheContainerReader reader)
    {
        if (!TryOpenColumns(reader, out MemoryMappedViewAccessor? addr, out MemoryMappedViewAccessor? mt,
                out MemoryMappedViewAccessor? size, out MemoryMappedViewAccessor? gen,
                out ulong[]? typeDictionary, out int typeIdWidth, out long recordCount))
            yield break;

        using MemoryMappedViewAccessor? addrDisp = addr;
        using MemoryMappedViewAccessor? mtDisp = mt;
        using MemoryMappedViewAccessor? sizeDisp = size;
        using MemoryMappedViewAccessor? genDisp = gen;

        foreach (HeapEntry entry in ReadColumnRange(addr!, mt!, size!, gen!, typeDictionary!, typeIdWidth, 0, recordCount))
            yield return entry;
    }

    // Internal static helper kept for call sites that don't need DI.
    internal static IEnumerable<HeapEntry> ReadDiskEntriesRange(string containerPath, long startRecord, long recordCount)
    {
        if (string.IsNullOrWhiteSpace(containerPath)
            || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader)
            || reader is null)
            yield break;

        foreach (HeapEntry entry in ReadDiskEntriesRange(reader, startRecord, recordCount))
            yield return entry;
    }

    /// <summary>
    /// Range counterpart of <see cref="ReadDiskEntries(CacheContainerReader)"/>. This is the one
    /// that mattered most: <c>HeapIndexScanDispatcher</c> opens one range enumeration per worker
    /// (8 on an 8-core box, 32 on a 32-core one) over <i>disjoint</i> slices, and the previous
    /// per-open verify re-hashed the whole section for each — 2.9 GB hashed to read 365 MB.
    /// With a shared session the first worker verifies and the rest proceed straight to reading.
    /// </summary>
    internal static IEnumerable<HeapEntry> ReadDiskEntriesRange(CacheContainerReader reader, long startRecord, long recordCount)
    {
        if (startRecord < 0 || recordCount <= 0)
            yield break;

        if (!TryOpenColumns(reader, out MemoryMappedViewAccessor? addr, out MemoryMappedViewAccessor? mt,
                out MemoryMappedViewAccessor? size, out MemoryMappedViewAccessor? gen,
                out ulong[]? typeDictionary, out int typeIdWidth, out long totalRecordCount))
            yield break;

        using MemoryMappedViewAccessor? addrDisp = addr;
        using MemoryMappedViewAccessor? mtDisp = mt;
        using MemoryMappedViewAccessor? sizeDisp = size;
        using MemoryMappedViewAccessor? genDisp = gen;

        long clampedCount = Math.Min(recordCount, totalRecordCount - startRecord);
        if (clampedCount <= 0)
            yield break;

        foreach (HeapEntry entry in ReadColumnRange(addr!, mt!, size!, gen!, typeDictionary!, typeIdWidth, startRecord, clampedCount))
            yield return entry;
    }

    /// <summary>
    /// Loads the <c>ObjectTypeDictionary</c> section as a flat <c>ulong[]</c> indexed by
    /// <c>TypeId</c>. Deliberately an array and not a <c>Dictionary</c>: it is indexed once per
    /// object in <see cref="ZeroCopyColumnReader.FillBatch"/>, so a hash lookup there would be
    /// per-object cost on the hottest loop in the codebase. 14,003 types is ~112 KB.
    /// </summary>
    private static bool TryLoadTypeDictionary(CacheContainerReader reader, out ulong[]? methodTables)
    {
        methodTables = null;

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectTypeDictionary, out MemoryMappedViewAccessor? dictAcc, out long dictLen)
            || dictAcc is null)
            return false;

        using (dictAcc)
        {
            if (dictLen <= 0 || dictLen % ColumnSize != 0)
                return false;

            var values = new ulong[dictLen / ColumnSize];
            for (int i = 0; i < values.Length; i++)
                values[i] = dictAcc.ReadUInt64(i * (long)ColumnSize);

            methodTables = values;
            return true;
        }
    }

    private static bool TryOpenColumns(
        CacheContainerReader reader,
        out MemoryMappedViewAccessor? addr,
        out MemoryMappedViewAccessor? mt,
        out MemoryMappedViewAccessor? size,
        out MemoryMappedViewAccessor? gen,
        out ulong[]? typeDictionary,
        out int typeIdWidth,
        out long recordCount)
    {
        addr = null;
        mt = null;
        size = null;
        gen = null;
        typeDictionary = null;
        typeIdWidth = 0;
        recordCount = 0;

        if (!TryLoadTypeDictionary(reader, out ulong[]? methodTables) || methodTables is null)
            return false;

        // Derived from the dictionary rather than stored: the writer picks the narrowest width the
        // distinct-type count allows, so the count determines it unambiguously.
        typeIdWidth = methodTables.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(uint);

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out MemoryMappedViewAccessor? addrAcc, out long addrLen))
            return false;
        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectMethodTables, out MemoryMappedViewAccessor? mtAcc, out long mtLen))
        {
            addrAcc?.Dispose();
            return false;
        }
        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectSizes, out MemoryMappedViewAccessor? sizeAcc, out long sizeLen))
        {
            addrAcc?.Dispose();
            mtAcc?.Dispose();
            return false;
        }
        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectGenerations, out MemoryMappedViewAccessor? genAcc, out long genLen))
        {
            addrAcc?.Dispose();
            mtAcc?.Dispose();
            sizeAcc?.Dispose();
            return false;
        }

        // The MethodTable column is TypeId-width now, so it no longer shares the 8-byte stride the
        // other two use — the cross-column check has to divide it by its own width.
        long candidateRecordCount = addrLen / ColumnSize;
        if (candidateRecordCount == 0 ||
            mtLen / typeIdWidth != candidateRecordCount || sizeLen / ColumnSize != candidateRecordCount ||
            genLen / GenColumnSize != candidateRecordCount)
        {
            addrAcc.Dispose();
            mtAcc.Dispose();
            sizeAcc.Dispose();
            genAcc.Dispose();
            return false;
        }

        addr = addrAcc;
        mt = mtAcc;
        size = sizeAcc;
        gen = genAcc;
        typeDictionary = methodTables;
        recordCount = candidateRecordCount;
        return true;
    }

    private static IEnumerable<HeapEntry> ReadColumnRange(
        MemoryMappedViewAccessor addr, MemoryMappedViewAccessor mt, MemoryMappedViewAccessor size, MemoryMappedViewAccessor gen,
        ulong[] typeDictionary, int typeIdWidth,
        long startRecord, long recordCount)
    {
        HeapEntry[] batch = System.Buffers.ArrayPool<HeapEntry>.Shared.Rent(BatchRecords);
        try
        {
            using var columnReader = new ZeroCopyColumnReader(addr, mt, size, gen, typeDictionary, typeIdWidth);
            long remaining = recordCount;
            long start = startRecord;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(BatchRecords, remaining);
                columnReader.FillBatch(start, batch, chunk);
                for (int i = 0; i < chunk; i++)
                    yield return batch[i];

                start += chunk;
                remaining -= chunk;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<HeapEntry>.Shared.Return(batch);
        }
    }

    // Holds raw pointers into the three mapped columns for the lifetime of a scan. Isolated in
    // its own (non-iterator) class because pointer types can't appear anywhere lexically inside
    // ReadDiskEntries above. Reading via Unsafe.ReadUnaligned in a tight batch loop avoids the
    // per-call bounds/alignment safety overhead that MemoryMappedViewAccessor.ReadUInt64 pays on
    // every single call — significant at the hundreds-of-millions-of-records scale this hits on
    // large dumps. This assumes a little-endian host (x64/ARM64), which is the only platform
    // this app targets.
    private sealed unsafe class ZeroCopyColumnReader : IDisposable
    {
        private readonly MemoryMappedViewAccessor _addr;
        private readonly MemoryMappedViewAccessor _mt;
        private readonly MemoryMappedViewAccessor _size;
        private readonly MemoryMappedViewAccessor _gen;
        private readonly byte* _addrPtr;
        private readonly byte* _mtPtr;
        private readonly byte* _sizePtr;
        private readonly byte* _genPtr;

        private readonly ulong[] _typeDictionary;
        private readonly int _typeIdWidth;

        public ZeroCopyColumnReader(
            MemoryMappedViewAccessor addr, MemoryMappedViewAccessor mt, MemoryMappedViewAccessor size, MemoryMappedViewAccessor gen,
            ulong[] typeDictionary, int typeIdWidth)
        {
            _addr = addr;
            _mt = mt;
            _size = size;
            _gen = gen;
            _typeDictionary = typeDictionary;
            _typeIdWidth = typeIdWidth;

            byte* p = null;
            _addr.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _addrPtr = p + _addr.PointerOffset;

            p = null;
            _mt.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _mtPtr = p + _mt.PointerOffset;

            p = null;
            _size.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _sizePtr = p + _size.PointerOffset;

            p = null;
            _gen.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _genPtr = p + _gen.PointerOffset;
        }

        public void FillBatch(long startIndex, HeapEntry[] destination, int count)
        {
            byte* addrBase = _addrPtr + startIndex * ColumnSize;
            byte* mtBase = _mtPtr + startIndex * _typeIdWidth;
            byte* sizeBase = _sizePtr + startIndex * ColumnSize;
            byte* genBase = _genPtr + startIndex * GenColumnSize;
            ulong[] dictionary = _typeDictionary;

            // Split on width outside the loop rather than inside it: the branch is loop-invariant,
            // and this is the per-object path for every enumeration in the process.
            if (_typeIdWidth == sizeof(ushort))
            {
                for (int i = 0; i < count; i++)
                {
                    int off = i * ColumnSize;
                    ulong address = Unsafe.ReadUnaligned<ulong>(addrBase + off);
                    ulong objSize = Unsafe.ReadUnaligned<ulong>(sizeBase + off);
                    ulong methodTable = dictionary[Unsafe.ReadUnaligned<ushort>(mtBase + i * sizeof(ushort))];
                    sbyte generation = unchecked((sbyte)genBase[i]);
                    destination[i] = new HeapEntry(address, methodTable, objSize, generation);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int off = i * ColumnSize;
                    ulong address = Unsafe.ReadUnaligned<ulong>(addrBase + off);
                    ulong objSize = Unsafe.ReadUnaligned<ulong>(sizeBase + off);
                    ulong methodTable = dictionary[Unsafe.ReadUnaligned<uint>(mtBase + i * sizeof(uint))];
                    sbyte generation = unchecked((sbyte)genBase[i]);
                    destination[i] = new HeapEntry(address, methodTable, objSize, generation);
                }
            }
        }

        public void Dispose()
        {
            _addr.SafeMemoryMappedViewHandle.ReleasePointer();
            _mt.SafeMemoryMappedViewHandle.ReleasePointer();
            _size.SafeMemoryMappedViewHandle.ReleasePointer();
            _gen.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
