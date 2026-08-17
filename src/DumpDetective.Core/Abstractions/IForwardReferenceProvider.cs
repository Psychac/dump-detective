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

    /// <summary>
    /// Allocation-free counterpart to <see cref="TryGetChildren"/>: writes the child addresses into
    /// <paramref name="buffer"/> (growing it if it's too small) and returns how many were written.
    /// Returns 0 when <paramref name="parent"/> has no recorded children.
    ///
    /// Exists because whole-graph consumers call this once per reachable node — millions of times —
    /// and only need the children long enough to copy them into their own structure.
    /// <see cref="TryGetChildren"/>'s freshly-allocated list per call is pure garbage in that
    /// pattern: on a 3.3GB dump it measured ~235MB of allocation for data that was dead within a few
    /// instructions. See docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 7.
    ///
    /// The default implementation delegates to <see cref="TryGetChildren"/>, so this is purely an
    /// opt-in optimization — an implementation that can't do better stays correct without change.
    /// </summary>
    int GetChildren(ulong parent, ref ulong[] buffer)
    {
        if (!TryGetChildren(parent, out IReadOnlyList<ulong> children) || children.Count == 0)
            return 0;

        if (buffer.Length < children.Count)
            buffer = new ulong[children.Count];

        for (int i = 0; i < children.Count; i++)
            buffer[i] = children[i];

        return children.Count;
    }
}
