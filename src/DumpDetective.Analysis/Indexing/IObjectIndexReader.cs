namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Reads <see cref="HeapEntry"/> records from a heap object index produced by a
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
    /// Streams all <see cref="HeapEntry"/> records from the binary index at
    /// <paramref name="indexPath"/>. Returns an empty sequence if the file does not
    /// exist or contains no records.
    /// </summary>
    IEnumerable<HeapEntry> ReadEntries(string indexPath);
}
