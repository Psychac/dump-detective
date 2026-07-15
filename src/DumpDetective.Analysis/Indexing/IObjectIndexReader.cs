namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads <see cref="HeapEntry"/> records from the <c>Objects</c> section of a
/// <c>cache.bin</c> container produced by a <c>DiskBackedObjectIndexWriter</c> or
/// compatible writer.
/// </summary>
/// <remarks>
/// Implementations use streaming reads — no full materialisation into memory.
/// Callers must consume entries via <c>foreach</c> / <c>yield</c> to preserve the
/// bounded-memory guarantee.
/// </remarks>
internal interface IObjectIndexReader
{
    /// <summary>
    /// Streams all <see cref="HeapEntry"/> records from the <c>Objects</c> section of
    /// the <c>cache.bin</c> container at <paramref name="containerPath"/>. Returns an
    /// empty sequence if the container is missing, has no <c>Objects</c> section, or
    /// contains no records.
    /// </summary>
    IEnumerable<HeapEntry> ReadEntries(string containerPath);
}
