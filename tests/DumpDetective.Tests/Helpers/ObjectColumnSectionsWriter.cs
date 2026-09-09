using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Columns;
using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Tests.Helpers;

/// <summary>
/// Writes a valid set of columnar object sections into a <see cref="CacheContainerWriter"/>.
/// </summary>
/// <remarks>
/// Exists because <c>ObjectMethodTables</c> stopped being a plain <c>ulong</c> column in format v5:
/// it now stores a narrow <c>TypeId</c> indexing <c>ObjectTypeDictionary</c>, so a test that writes
/// the two independently can easily produce a container the reader rejects for reasons unrelated to
/// what it is testing. Centralising it here means the next format change touches one place instead
/// of the six test files that previously each kept their own copy of this logic.
/// </remarks>
internal static class ObjectColumnSectionsWriter
{
    /// <summary>
    /// Writes <c>ObjectAddresses</c>, <c>ObjectTypeDictionary</c>, <c>ObjectMethodTables</c>,
    /// <c>ObjectSizes</c> and (when <paramref name="includeGenerations"/>) <c>ObjectGenerationRuns</c>,
    /// deriving the type dictionary from the distinct method tables in <paramref name="records"/>.
    /// </summary>
    public static void Write(
        CacheContainerWriter writer,
        IReadOnlyList<(ulong Address, ulong MethodTable, ulong Size, sbyte Generation)> records,
        bool includeGenerations = true)
    {
        WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, records.Select(r => r.Address).ToArray());
        WriteMethodTableColumns(writer, records.Select(r => r.MethodTable).ToArray());
        WriteUlongColumn(writer, CacheSectionId.ObjectSizes, records.Select(r => r.Size).ToArray());

        if (includeGenerations)
            WriteGenerationColumn(writer, records.Select(r => r.Generation).ToArray());
    }

    /// <summary>
    /// Writes the type dictionary and the narrow <c>TypeId</c> column that together replace the old
    /// 8-byte <c>MethodTable</c> column. Width is derived from the distinct count exactly as the
    /// production writer and reader derive it.
    /// </summary>
    public static void WriteMethodTableColumns(CacheContainerWriter writer, ulong[] methodTables)
    {
        ulong[] dictionary = methodTables.Distinct().OrderBy(mt => mt).ToArray();
        var typeIdByMethodTable = new Dictionary<ulong, int>(dictionary.Length);
        for (int i = 0; i < dictionary.Length; i++)
            typeIdByMethodTable[dictionary[i]] = i;

        WriteUlongColumn(writer, CacheSectionId.ObjectTypeDictionary, dictionary);

        int width = dictionary.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(uint);
        byte[] buffer = new byte[methodTables.Length * width];
        for (int i = 0; i < methodTables.Length; i++)
        {
            int typeId = typeIdByMethodTable[methodTables[i]];
            if (width == sizeof(ushort))
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(i * sizeof(ushort)), (ushort)typeId);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * sizeof(uint)), (uint)typeId);
        }

        writer.BeginSection(CacheSectionId.ObjectMethodTables);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(methodTables.Length);
    }

    /// <summary>
    /// Writes <c>ObjectAddresses</c> block-delta encoded plus its <c>ObjectAddressBlockBases</c> and
    /// <c>ObjectAddressOverflow</c> sections, the way the format-v6 writer does. Shares
    /// <see cref="BlockDeltaColumn.Encode"/> with production rather than re-deriving the encoding,
    /// so a change to it can't leave the tests validating the old shape.
    /// </summary>
    public static void WriteBlockDeltaAddressColumns(CacheContainerWriter writer, ulong[] addresses)
    {
        var blockBases = new List<ulong>();
        var overflow = new List<(uint RecordIndex, ulong Value)>();
        byte[] buffer = new byte[addresses.Length * BlockDeltaColumn.DeltaWidth];

        for (int i = 0; i < addresses.Length; i++)
        {
            uint delta = BlockDeltaColumn.Encode(addresses[i], i, blockBases, overflow);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * BlockDeltaColumn.DeltaWidth), delta);
        }

        writer.BeginSection(CacheSectionId.ObjectAddresses);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(addresses.Length);

        writer.BeginSection(CacheSectionId.ObjectAddressBlockBases);
        uint basesChecksum = BlockDeltaColumn.WriteBlockBases(writer.Stream, blockBases, 64 * 1024);
        writer.EndSection(blockBases.Count, basesChecksum);

        writer.BeginSection(CacheSectionId.ObjectAddressOverflow);
        uint overflowChecksum = ColumnOverflowTable.Write(writer.Stream, overflow, 64 * 1024);
        writer.EndSection(overflow.Count, overflowChecksum);
    }

    /// <summary>
    /// Writes <c>ObjectSizes</c> narrowed to <paramref name="width"/> bytes per record plus the
    /// matching <c>ObjectSizeOverflow</c> section, the way the format-v6 writer does. The full
    /// 8-byte form stays available through <see cref="WriteUlongColumn"/>, since v6 readers accept
    /// both.
    /// </summary>
    public static void WriteNarrowedSizeColumns(CacheContainerWriter writer, ulong[] sizes, int width)
    {
        ulong sentinel = width == sizeof(ushort) ? ushort.MaxValue : uint.MaxValue;
        var overflow = new List<(uint RecordIndex, ulong Value)>();
        byte[] buffer = new byte[sizes.Length * width];

        for (int i = 0; i < sizes.Length; i++)
        {
            ulong stored = sizes[i];
            if (stored >= sentinel)
            {
                overflow.Add(((uint)i, sizes[i]));
                stored = sentinel;
            }

            if (width == sizeof(ushort))
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(i * sizeof(ushort)), (ushort)stored);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * sizeof(uint)), (uint)stored);
        }

        writer.BeginSection(CacheSectionId.ObjectSizes);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(sizes.Length);

        byte[] overflowBuffer = new byte[overflow.Count * 12];
        for (int i = 0; i < overflow.Count; i++)
        {
            Span<byte> slot = overflowBuffer.AsSpan(i * 12);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, overflow[i].RecordIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[sizeof(uint)..], overflow[i].Value);
        }

        writer.BeginSection(CacheSectionId.ObjectSizeOverflow);
        writer.Stream.Write(overflowBuffer, 0, overflowBuffer.Length);
        writer.EndSection(overflow.Count);
    }

    /// <summary>
    /// Writes the pair that <c>DominatorRowIndex</c> derives the reachable-row space from since
    /// format v10 (docs/cache/cache-ideal-design.md §3.1, R1): <c>ObjectAddresses</c> for the
    /// addresses, and <c>ReachableRowBitmap</c> for membership. Replaces the single
    /// <c>DominatorReachableAddresses</c> column tests used to write.
    /// </summary>
    /// <remarks>
    /// Every object row is marked reachable, so reachable row == object row and the row ordering
    /// every caller previously asserted against is preserved exactly. Writes the address column at
    /// the full 8-byte width, which <c>MonotonicAddressColumn</c> accepts alongside the
    /// block-delta form the production writer emits — no need to reproduce the encoding here.
    ///
    /// Use this *instead of* <see cref="Write"/>, not alongside it: both write
    /// <c>ObjectAddresses</c>, and the container is write-once per section.
    /// </remarks>
    public static void WriteReachableRows(CacheContainerWriter writer, IReadOnlyList<ulong> sortedAddresses)
    {
        WriteUlongColumn(writer, CacheSectionId.ObjectAddresses, sortedAddresses.ToArray());

        var allRows = new long[sortedAddresses.Count];
        for (int i = 0; i < allRows.Length; i++)
            allRows[i] = i;

        writer.BeginSection(CacheSectionId.ReachableRowBitmap);
        uint checksum = ReachableRowBitmap.Write(writer.Stream, sortedAddresses.Count, allRows);
        writer.EndSection(sortedAddresses.Count, checksum);
    }

    public static void WriteUlongColumn(CacheContainerWriter writer, CacheSectionId id, ulong[] values)
    {
        byte[] buffer = new byte[values.Length * sizeof(ulong)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(i * sizeof(ulong)), values[i]);

        writer.BeginSection(id);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(values.Length);
    }

    /// <summary>
    /// Writes the run-length encoded generation section (format v9). Kept named after the column it
    /// replaced so the six call sites don't all have to change; the encoding is the writer's concern.
    /// </summary>
    public static void WriteGenerationColumn(CacheContainerWriter writer, sbyte[] generations)
    {
        var runs = new List<(long FirstRecordIndex, sbyte Generation)>();
        for (int i = 0; i < generations.Length; i++)
        {
            if (i == 0 || generations[i] != generations[i - 1])
                runs.Add((i, generations[i]));
        }

        writer.BeginSection(CacheSectionId.ObjectGenerationRuns);
        uint checksum = ObjectGenerationRunTable.Write(writer.Stream, runs);
        writer.EndSection(runs.Count, checksum);
    }
}
