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
}
