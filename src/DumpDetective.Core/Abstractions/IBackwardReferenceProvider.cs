namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Reverse-lookup counterpart to <see cref="IReferenceProvider"/>: "who points at this object?"
/// Backed by the disk-backed reverse-reference index when available (see
/// docs/analysis/phase1-redesigns/full-reverse-index-plan.md); there is no in-memory equivalent —
/// enumerating "who points at X" without a precomputed index would require scanning the whole
/// heap per query, so callers must treat a missing provider as "no backward lookup available"
/// and fall back to a different search strategy rather than simulate one.
/// </summary>
public interface IBackwardReferenceProvider
{
    /// <summary>
    /// Retrieves all recorded parent addresses for <paramref name="child"/>.
    /// Returns <c>false</c> if <paramref name="child"/> has no recorded parents; <paramref name="parents"/>
    /// is empty and <paramref name="truncated"/> is <c>false</c> in that case.
    /// When <paramref name="truncated"/> is <c>true</c>, <paramref name="child"/> hit the index's
    /// fanout cap during extraction — <paramref name="parents"/> holds only a bounded subset, not
    /// every real parent.
    /// </summary>
    bool TryGetParents(ulong child, out IReadOnlyList<ulong> parents, out bool truncated);
}
