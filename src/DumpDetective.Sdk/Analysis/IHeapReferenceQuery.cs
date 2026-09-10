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
/// <remarks>
/// Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming surface.
///
/// Method named <see cref="EnumerateReverseReferences"/>, not <c>EnumerateReferrers</c> — renamed
/// 2026-09-10 (docs/refactor/modularity/phase-1-sdk-review-findings.md item 18) to match this
/// interface's own naming pattern instead of introducing a third word root. Unlike
/// <see cref="IHeapReferenceQuery.EnumerateReferences"/>, which deliberately mirrors the real
/// ClrMD API it wraps (<c>ClrObject.EnumerateReferences(carefully: true)</c>, used at 18+ call
/// sites across the dump-side codebase) and was left untouched for that reason, there is no single
/// ClrMD method this one mirrors — reverse/backward lookup is this project's own built index, so
/// there was no external naming parity to preserve here.
/// </remarks>
public interface IHeapReverseReferenceQuery
{
    IEnumerable<ulong> EnumerateReverseReferences(ulong address);
}
