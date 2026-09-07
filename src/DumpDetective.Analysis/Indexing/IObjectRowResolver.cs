namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// The <c>address ↔ object row</c> mapping, which is the single object identity everything after
/// format v10 keys off (docs/cache/cache-ideal-design.md §3.1, R1).
/// </summary>
/// <remarks>
/// An interface only so the row-keyed walk can be exercised against a synthetic graph in a unit
/// test — the production implementation, <see cref="ScratchFileObjectMetadataLookup"/>, needs real
/// per-segment scratch files on disk, which a test of graph traversal has no business constructing.
/// There is exactly one production implementation and no dispatch cost worth worrying about on the
/// walk's hot path, since the call is monomorphic there.
/// </remarks>
internal interface IObjectRowResolver
{
    /// <summary>The row holding <paramref name="address"/>, or -1 when it is not a live object.</summary>
    long TryGetRow(ulong address);

    /// <summary>The address at <paramref name="globalRow"/>.</summary>
    ulong GetAddress(long globalRow);
}
