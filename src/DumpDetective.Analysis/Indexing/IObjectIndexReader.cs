namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads <see cref="HeapEntry"/> records from the columnar ObjectAddresses/ObjectMethodTables/
/// ObjectSizes sections of a <c>cache.bin</c> container produced by a
/// <c>DiskBackedObjectIndexWriter</c> or compatible writer.
/// </summary>
/// <remarks>
/// Implementations use streaming reads — no full materialisation into memory.
/// Callers must consume entries via <c>foreach</c> / <c>yield</c> to preserve the
/// bounded-memory guarantee.
/// </remarks>
internal interface IObjectIndexReader
{
    /// <summary>
    /// Streams all <see cref="HeapEntry"/> records reconstructed by zipping together the
    /// columnar object sections of the <c>cache.bin</c> container at
    /// <paramref name="containerPath"/>. Returns an empty sequence if the container is
    /// missing, has no object sections, or contains no records.
    /// </summary>
    IEnumerable<HeapEntry> ReadEntries(string containerPath);

    /// <summary>
    /// Streams <paramref name="recordCount"/> <see cref="HeapEntry"/> records starting at
    /// <paramref name="startRecord"/> (0-based) from the <c>cache.bin</c> container at
    /// <paramref name="containerPath"/>. Returns an empty sequence if the container is missing,
    /// has no object sections, or contains no records. Used to partition a shared scan pass
    /// into disjoint contiguous ranges for concurrent reads.
    /// </summary>
    IEnumerable<HeapEntry> ReadEntriesRange(string containerPath, long startRecord, long recordCount);

    /// <summary>
    /// One-shot random-access lookup of a single <paramref name="address"/>'s
    /// <c>(MethodTable, Size)</c> via the disk-backed <c>SegmentIndex</c> — see
    /// <see cref="ObjectAddressLookup"/> and docs/cache/19-ObjectAddressLookupIndex.md. Opens,
    /// looks up once, and disposes; callers making many lookups over one analysis run should hold
    /// their own <see cref="ObjectAddressLookup"/> instance instead of calling this repeatedly.
    /// Returns <c>false</c> — not an error — when the container/section is unavailable or
    /// <paramref name="address"/> isn't found.
    /// </summary>
    bool TryGetEntry(string containerPath, ulong address, out ulong methodTable, out ulong size);
}
