namespace DumpDetective.Sdk.Analysis;

/// <summary>The <c>heap.references</c> capability — "what does this object point at?", mirroring
/// <c>IHeapAnalysisCache.TryGetForwardIndexProvider</c>'s <c>IForwardReferenceProvider</c>, re-signed
/// to take a bare address instead of <c>ClrHeap</c>.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapReferenceQuery
{
    IEnumerable<ulong> EnumerateReferences(ulong address);
}

/// <summary>The <c>heap.reverse-references</c> capability — "who points at this object?", mirroring
/// <c>IHeapAnalysisCache.TryGetReverseIndexProvider</c>'s <c>IBackwardReferenceProvider</c>.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapReverseReferenceQuery
{
    IEnumerable<ulong> EnumerateReferrers(ulong address);
}
