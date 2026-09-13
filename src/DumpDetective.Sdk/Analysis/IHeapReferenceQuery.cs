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

    /// <summary>
    /// The name of the instance field on <paramref name="parentAddress"/> that holds a reference to
    /// <paramref name="childAddress"/>, mirroring <c>ClrObject.EnumerateReferencesWithFields</c> —
    /// the same ClrMD API SOS's <c>!gcroot</c> draws field names from. Added 2026-09-13 for
    /// <c>ReferenceChainAnalyzer</c>'s retyping: a narrow, single-pair lookup (not a general
    /// "enumerate every reference with its field name" surface) because that's the only real need
    /// so far — resolving the field connecting the last two hops of an already-found root path.
    /// Returns <c>false</c> (with a <c>null</c> <paramref name="fieldName"/>) when
    /// <paramref name="parentAddress"/> isn't a valid object, the edge isn't present, or the edge is
    /// via an array element or dependent handle rather than a named instance field.
    /// </summary>
    bool TryGetReferenceFieldName(ulong parentAddress, ulong childAddress, out string? fieldName);
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

    /// <summary>
    /// Every recorded parent of <paramref name="address"/> plus whether the reverse index's own
    /// fanout cap truncated that list — mirrors <c>IBackwardReferenceProvider.TryGetParents</c>
    /// exactly. Added 2026-09-12 for <c>CollectionAnalyzer</c>'s retyping (first real consumer of
    /// this interface): unlike <see cref="EnumerateReverseReferences"/>, a caller reporting "N+
    /// referrers" when the index's answer is a lower bound, not the true count, needs to know that
    /// up front rather than after silently under-counting. Returns <c>false</c> (with an empty list
    /// and <paramref name="truncated"/> <c>false</c>) when no reverse index is available or
    /// <paramref name="address"/> has no recorded parents.
    /// </summary>
    bool TryGetReverseReferences(ulong address, out IReadOnlyList<ulong> parents, out bool truncated);

    /// <summary>
    /// Every object the reverse index recorded at least one incoming reference for, paired with its
    /// total incoming-reference count — mirrors <c>IBackwardReferenceProvider.EnumerateChildCounts</c>.
    /// Added 2026-09-13 for <c>DominatorAnalyzer</c>'s retyping: its primary (index-backed) leak-signal
    /// pass needs one full sequential scan of the whole reverse index's fan-in counts, a genuinely
    /// different shape from every prior consumer's per-address lookups
    /// (<see cref="EnumerateReverseReferences"/>/<see cref="TryGetReverseReferences"/>). Yields nothing
    /// when no reverse index is available for this session — same "unavailable" convention as the rest
    /// of this interface.
    /// </summary>
    IEnumerable<(ulong Address, int IncomingCount)> EnumerateIncomingReferenceCounts();
}
