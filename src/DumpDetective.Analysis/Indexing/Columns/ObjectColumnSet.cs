using System.IO.MemoryMappedFiles;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Columns;

/// <summary>
/// The four per-object columns opened together, with the encoding parameters a reader needs to
/// decode them: the <c>TypeId</c> dictionary and its width, and the <c>ObjectSizes</c> width plus
/// its escape table. Opening them as a set keeps the cross-column record-count agreement — the only
/// structural check these sections get — in one place.
/// </summary>
/// <remarks>
/// Neither width is stored in the container. Both are recovered from the TOC's per-section
/// <c>Length</c> and <c>RecordCount</c>, which is why the writer is free to choose the narrowest
/// width a given dump allows without a format flag (docs/cache/cache-format-clean-slate-redesign.md
/// §10.1).
/// </remarks>
internal sealed class ObjectColumnSet : IDisposable
{
    private const int AddressWidth = sizeof(ulong);
    private const int GenerationWidth = sizeof(sbyte);

    public required MemoryMappedViewAccessor Addresses { get; init; }
    public required MemoryMappedViewAccessor MethodTables { get; init; }
    public required MemoryMappedViewAccessor Sizes { get; init; }
    public required MemoryMappedViewAccessor Generations { get; init; }

    /// <summary>TypeId → MethodTable, indexed once per object; see <see cref="CacheSectionId.ObjectTypeDictionary"/>.</summary>
    public required ulong[] TypeDictionary { get; init; }

    public required int TypeIdWidth { get; init; }
    public required int SizeWidth { get; init; }
    public required ColumnOverflowTable SizeOverflow { get; init; }
    public required long RecordCount { get; init; }

    public static bool TryOpen(CacheContainerReader reader, out ObjectColumnSet? columns)
    {
        columns = null;

        if (!TryLoadTypeDictionary(reader, out ulong[]? typeDictionary) || typeDictionary is null)
            return false;

        // Derived from the dictionary rather than stored: the writer picks the narrowest width the
        // distinct-type count allows, so the count determines it unambiguously.
        int typeIdWidth = typeDictionary.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(uint);

        MemoryMappedViewAccessor? addresses = null;
        MemoryMappedViewAccessor? methodTables = null;
        MemoryMappedViewAccessor? sizes = null;
        MemoryMappedViewAccessor? generations = null;

        try
        {
            if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectAddresses, out addresses, out long addressesLength)
                || addresses is null
                || !reader.TryOpenSectionAccessor(CacheSectionId.ObjectMethodTables, out methodTables, out long methodTablesLength)
                || methodTables is null
                || !reader.TryOpenSectionAccessor(CacheSectionId.ObjectSizes, out sizes, out long sizesLength)
                || sizes is null
                || !reader.TryOpenSectionAccessor(CacheSectionId.ObjectGenerations, out generations, out long generationsLength)
                || generations is null)
            {
                return false;
            }

            long recordCount = addressesLength / AddressWidth;
            if (recordCount == 0
                || methodTablesLength / typeIdWidth != recordCount
                || generationsLength / GenerationWidth != recordCount)
            {
                return false;
            }

            // The size column no longer shares the address column's stride, so its width comes from
            // its own length. A width the format doesn't define means a container this build can't
            // read, which is a cold cache rather than an error.
            int sizeWidth = sizesLength % recordCount == 0 ? (int)(sizesLength / recordCount) : 0;
            if (!NarrowColumnWidth.IsSupported(sizeWidth))
                return false;

            ColumnOverflowTable sizeOverflow = ColumnOverflowTable.Empty;
            if (sizeWidth != NarrowColumnWidth.Full)
            {
                // A narrowed column without its escape table would report sentinels as real sizes,
                // so this is the one absence that has to invalidate the whole set rather than
                // degrade.
                if (!ColumnOverflowTable.TryLoad(reader, CacheSectionId.ObjectSizeOverflow, out ColumnOverflowTable? loaded)
                    || loaded is null)
                {
                    return false;
                }

                sizeOverflow = loaded;
            }

            columns = new ObjectColumnSet
            {
                Addresses = addresses,
                MethodTables = methodTables,
                Sizes = sizes,
                Generations = generations,
                TypeDictionary = typeDictionary,
                TypeIdWidth = typeIdWidth,
                SizeWidth = sizeWidth,
                SizeOverflow = sizeOverflow,
                RecordCount = recordCount,
            };
            return true;
        }
        finally
        {
            if (columns is null)
            {
                addresses?.Dispose();
                methodTables?.Dispose();
                sizes?.Dispose();
                generations?.Dispose();
            }
        }
    }

    /// <summary>
    /// Loads the <c>ObjectTypeDictionary</c> section as a flat <c>ulong[]</c> indexed by
    /// <c>TypeId</c>. Deliberately an array and not a <c>Dictionary</c>: it is indexed once per
    /// object on the hottest loop in the codebase, so a hash lookup there would be per-object cost.
    /// 14,003 types is ~112 KB.
    /// </summary>
    private static bool TryLoadTypeDictionary(CacheContainerReader reader, out ulong[]? methodTables)
    {
        methodTables = null;

        if (!reader.TryOpenSectionAccessor(CacheSectionId.ObjectTypeDictionary, out MemoryMappedViewAccessor? dictionary, out long length)
            || dictionary is null)
            return false;

        using (dictionary)
        {
            if (length <= 0 || length % sizeof(ulong) != 0)
                return false;

            var values = new ulong[length / sizeof(ulong)];
            for (int i = 0; i < values.Length; i++)
                values[i] = dictionary.ReadUInt64(i * (long)sizeof(ulong));

            methodTables = values;
            return true;
        }
    }

    public void Dispose()
    {
        Addresses.Dispose();
        MethodTables.Dispose();
        Sizes.Dispose();
        Generations.Dispose();
    }
}
