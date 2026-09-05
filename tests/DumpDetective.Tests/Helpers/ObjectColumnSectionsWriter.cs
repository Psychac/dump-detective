using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Container;

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
    /// <c>ObjectSizes</c> and (when <paramref name="includeGenerations"/>) <c>ObjectGenerations</c>,
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

    public static void WriteUlongColumn(CacheContainerWriter writer, CacheSectionId id, ulong[] values)
    {
        byte[] buffer = new byte[values.Length * sizeof(ulong)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(i * sizeof(ulong)), values[i]);

        writer.BeginSection(id);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(values.Length);
    }

    public static void WriteGenerationColumn(CacheContainerWriter writer, sbyte[] generations)
    {
        byte[] buffer = new byte[generations.Length];
        for (int i = 0; i < generations.Length; i++)
            buffer[i] = unchecked((byte)generations[i]);

        writer.BeginSection(CacheSectionId.ObjectGenerations);
        writer.Stream.Write(buffer, 0, buffer.Length);
        writer.EndSection(generations.Length);
    }
}
