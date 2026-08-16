namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Forward-lookup counterpart to <see cref="IBackwardReferenceProvider"/>: "what does this object
/// point at?" Backed by the disk-backed forward-reference index when available (see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D5) — a new, separate index
/// from the existing capped reverse-reference index, uncapped, since out-degree has no hub-fanout
/// problem the way in-degree does. There is no in-memory equivalent — callers must treat a missing
/// provider as "no forward-index lookup available" and fall back to a live
/// <c>ClrObject.EnumerateReferences(carefully: true)</c> walk.
/// </summary>
public interface IForwardReferenceProvider
{
    /// <summary>
    /// Retrieves all recorded child addresses for <paramref name="parent"/>. Returns <c>false</c>
    /// if <paramref name="parent"/> has no recorded children (not present in the index — this is
    /// the common case for leaf objects, not an error); <paramref name="children"/> is empty in
    /// that case. Never truncated, unlike <see cref="IBackwardReferenceProvider.TryGetParents"/>.
    /// </summary>
    bool TryGetChildren(ulong parent, out IReadOnlyList<ulong> children);
}
