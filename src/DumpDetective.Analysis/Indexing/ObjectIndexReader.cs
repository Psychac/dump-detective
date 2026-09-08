using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing;

internal sealed class ObjectIndexReader : IObjectIndexReader
{
    /// <summary>Shared singleton — stateless, safe to reuse across calls.</summary>
    internal static readonly ObjectIndexReader Instance = new();

    private const int ColumnSize = sizeof(ulong);

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
        if (!ObjectColumnSet.TryOpen(reader, out ObjectColumnSet? columns) || columns is null)
            yield break;

        using ObjectColumnSet ownedColumns = columns;

        foreach (HeapEntry entry in ReadColumnRange(ownedColumns, 0, ownedColumns.RecordCount))
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

        if (!ObjectColumnSet.TryOpen(reader, out ObjectColumnSet? columns) || columns is null)
            yield break;

        using ObjectColumnSet ownedColumns = columns;

        long clampedCount = Math.Min(recordCount, ownedColumns.RecordCount - startRecord);
        if (clampedCount <= 0)
            yield break;

        foreach (HeapEntry entry in ReadColumnRange(ownedColumns, startRecord, clampedCount))
            yield return entry;
    }

    private static IEnumerable<HeapEntry> ReadColumnRange(ObjectColumnSet columns, long startRecord, long recordCount)
    {
        HeapEntry[] batch = System.Buffers.ArrayPool<HeapEntry>.Shared.Rent(BatchRecords);
        try
        {
            using var columnReader = new ZeroCopyColumnReader(columns, startRecord);
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
        private readonly ObjectColumnSet _columns;
        private readonly byte* _addrPtr;
        private readonly byte* _mtPtr;
        private readonly byte* _sizePtr;

        private readonly ulong[] _typeDictionary;
        private readonly int _typeIdWidth;
        private readonly int _sizeWidth;
        private readonly int _addressWidth;
        private readonly BlockDeltaColumn? _addressDeltas;
        private ColumnOverflowTable.Cursor _sizeOverflowCursor;
        private ColumnOverflowTable.Cursor _addressOverflowCursor;
        // Generation is run-length encoded rather than a column, so it advances with the batch
        // instead of being indexed — see ObjectGenerationRunTable.
        private ObjectGenerationRunTable.Cursor _generationCursor;

        public ZeroCopyColumnReader(ObjectColumnSet columns, long startRecord)
        {
            _columns = columns;
            _typeDictionary = columns.TypeDictionary;
            _typeIdWidth = columns.TypeIdWidth;
            _sizeWidth = columns.SizeWidth;
            _addressWidth = columns.AddressWidth;
            _addressDeltas = columns.AddressDeltas;
            _sizeOverflowCursor = columns.SizeOverflow.OpenCursor(startRecord);
            _addressOverflowCursor = columns.AddressDeltas?.OpenCursor(startRecord) ?? default;
            _generationCursor = columns.GenerationRuns.OpenCursor(startRecord);

            byte* p = null;
            columns.Addresses.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _addrPtr = p + columns.Addresses.PointerOffset;

            p = null;
            columns.MethodTables.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _mtPtr = p + columns.MethodTables.PointerOffset;

            p = null;
            columns.Sizes.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _sizePtr = p + columns.Sizes.PointerOffset;
        }

        public void FillBatch(long startIndex, HeapEntry[] destination, int count)
        {
            byte* addrBase = _addrPtr + startIndex * _addressWidth;
            byte* mtBase = _mtPtr + startIndex * _typeIdWidth;
            byte* sizeBase = _sizePtr + startIndex * _sizeWidth;
            ulong[] dictionary = _typeDictionary;

            // Split on width outside the loop rather than inside it: the branch is loop-invariant,
            // and this is the per-object path for every enumeration in the process. The size
            // column's own width branch stays inside ReadSize — splitting on that too would mean
            // six copies of this loop, and the narrowing it enables removes far more read traffic
            // per record than the branch costs.
            if (_typeIdWidth == sizeof(ushort))
            {
                for (int i = 0; i < count; i++)
                {
                    ulong address = ReadAddress(addrBase, i, startIndex + i);
                    ulong objSize = ReadSize(sizeBase, i, startIndex + i);
                    ulong methodTable = dictionary[Unsafe.ReadUnaligned<ushort>(mtBase + i * sizeof(ushort))];
                    sbyte generation = _generationCursor.Read(startIndex + i);
                    destination[i] = new HeapEntry(address, methodTable, objSize, generation);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ulong address = ReadAddress(addrBase, i, startIndex + i);
                    ulong objSize = ReadSize(sizeBase, i, startIndex + i);
                    ulong methodTable = dictionary[Unsafe.ReadUnaligned<uint>(mtBase + i * sizeof(uint))];
                    sbyte generation = _generationCursor.Read(startIndex + i);
                    destination[i] = new HeapEntry(address, methodTable, objSize, generation);
                }
            }
        }

        private ulong ReadAddress(byte* addrBase, int offsetInBatch, long recordIndex)
        {
            if (_addressDeltas is null)
                return Unsafe.ReadUnaligned<ulong>(addrBase + offsetInBatch * ColumnSize);

            uint delta = Unsafe.ReadUnaligned<uint>(addrBase + offsetInBatch * BlockDeltaColumn.DeltaWidth);
            return _addressDeltas.Decode(delta, recordIndex, ref _addressOverflowCursor);
        }

        private ulong ReadSize(byte* sizeBase, int offsetInBatch, long recordIndex)
        {
            if (_sizeWidth == sizeof(ushort))
            {
                ushort narrow = Unsafe.ReadUnaligned<ushort>(sizeBase + offsetInBatch * sizeof(ushort));
                return narrow == ushort.MaxValue ? _sizeOverflowCursor.Read(recordIndex) : narrow;
            }

            if (_sizeWidth == sizeof(uint))
            {
                uint narrow = Unsafe.ReadUnaligned<uint>(sizeBase + offsetInBatch * sizeof(uint));
                return narrow == uint.MaxValue ? _sizeOverflowCursor.Read(recordIndex) : narrow;
            }

            return Unsafe.ReadUnaligned<ulong>(sizeBase + offsetInBatch * ColumnSize);
        }

        public void Dispose()
        {
            _columns.Addresses.SafeMemoryMappedViewHandle.ReleasePointer();
            _columns.MethodTables.SafeMemoryMappedViewHandle.ReleasePointer();
            _columns.Sizes.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
