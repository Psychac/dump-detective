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

    // Internal static helper kept for call sites that don't need DI.
    internal static IEnumerable<HeapEntry> ReadDiskEntries(string containerPath)
    {
        if (!TryOpenColumns(containerPath, out MemoryMappedViewAccessor? addr, out MemoryMappedViewAccessor? mt,
                out MemoryMappedViewAccessor? size, out MemoryMappedViewAccessor? gen, out long recordCount))
            yield break;

        using MemoryMappedViewAccessor? addrDisp = addr;
        using MemoryMappedViewAccessor? mtDisp = mt;
        using MemoryMappedViewAccessor? sizeDisp = size;
        using MemoryMappedViewAccessor? genDisp = gen;

        foreach (HeapEntry entry in ReadColumnRange(addr!, mt!, size!, gen!, 0, recordCount))
            yield return entry;
    }

    // Internal static helper kept for call sites that don't need DI.
    internal static IEnumerable<HeapEntry> ReadDiskEntriesRange(string containerPath, long startRecord, long recordCount)
    {
        if (startRecord < 0 || recordCount <= 0)
            yield break;

        if (!TryOpenColumns(containerPath, out MemoryMappedViewAccessor? addr, out MemoryMappedViewAccessor? mt,
                out MemoryMappedViewAccessor? size, out MemoryMappedViewAccessor? gen, out long totalRecordCount))
            yield break;

        using MemoryMappedViewAccessor? addrDisp = addr;
        using MemoryMappedViewAccessor? mtDisp = mt;
        using MemoryMappedViewAccessor? sizeDisp = size;
        using MemoryMappedViewAccessor? genDisp = gen;

        long clampedCount = Math.Min(recordCount, totalRecordCount - startRecord);
        if (clampedCount <= 0)
            yield break;

        foreach (HeapEntry entry in ReadColumnRange(addr!, mt!, size!, gen!, startRecord, clampedCount))
            yield return entry;
    }

    private static bool TryOpenColumns(
        string containerPath,
        out MemoryMappedViewAccessor? addr,
        out MemoryMappedViewAccessor? mt,
        out MemoryMappedViewAccessor? size,
        out MemoryMappedViewAccessor? gen,
        out long recordCount)
    {
        addr = null;
        mt = null;
        size = null;
        gen = null;
        recordCount = 0;

        if (string.IsNullOrWhiteSpace(containerPath) || !CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return false;

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

        long candidateRecordCount = addrLen / ColumnSize;
        if (candidateRecordCount == 0 ||
            mtLen / ColumnSize != candidateRecordCount || sizeLen / ColumnSize != candidateRecordCount ||
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
        recordCount = candidateRecordCount;
        return true;
    }

    private static IEnumerable<HeapEntry> ReadColumnRange(
        MemoryMappedViewAccessor addr, MemoryMappedViewAccessor mt, MemoryMappedViewAccessor size, MemoryMappedViewAccessor gen,
        long startRecord, long recordCount)
    {
        HeapEntry[] batch = System.Buffers.ArrayPool<HeapEntry>.Shared.Rent(BatchRecords);
        try
        {
            using var columnReader = new ZeroCopyColumnReader(addr, mt, size, gen);
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

        public ZeroCopyColumnReader(MemoryMappedViewAccessor addr, MemoryMappedViewAccessor mt, MemoryMappedViewAccessor size, MemoryMappedViewAccessor gen)
        {
            _addr = addr;
            _mt = mt;
            _size = size;
            _gen = gen;

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
            byte* mtBase = _mtPtr + startIndex * ColumnSize;
            byte* sizeBase = _sizePtr + startIndex * ColumnSize;
            byte* genBase = _genPtr + startIndex * GenColumnSize;

            for (int i = 0; i < count; i++)
            {
                int off = i * ColumnSize;
                ulong address = Unsafe.ReadUnaligned<ulong>(addrBase + off);
                ulong methodTable = Unsafe.ReadUnaligned<ulong>(mtBase + off);
                ulong objSize = Unsafe.ReadUnaligned<ulong>(sizeBase + off);
                sbyte generation = unchecked((sbyte)genBase[i]);
                destination[i] = new HeapEntry(address, methodTable, objSize, generation);
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
