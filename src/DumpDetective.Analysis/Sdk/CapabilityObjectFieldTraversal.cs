using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// The capability-scoped counterpart to <c>Traversal.ObjectGraphTraversal.TryFindByPredicate</c> —
/// same prioritized-named-field-first, generic-reference-fallback search, ported against
/// <see cref="IDumpObjectFieldQuery"/>/<see cref="IHeapObjectLookup"/>/<see cref="IHeapReferenceQuery"/>
/// instead of a raw <c>ClrObject</c>. Built for <c>AsyncTaskAnalyzer</c>'s retyping (its
/// faulted-task exception-object search), the first Tier-2 consumer — extracted directly (not
/// generalized behind a delegate parameter the way the pre-retyping helper's <c>readObjectField</c>
/// was) since every real caller today reads named fields the same way, through
/// <see cref="IDumpObjectFieldQuery.TryReadField"/>.
/// </summary>
internal static class CapabilityObjectFieldTraversal
{
    /// <summary>
    /// Depth-first search from <paramref name="sourceAddress"/>: tries <paramref name="prioritizedFieldNames"/>
    /// (in order) as object-reference fields on the current node before falling back to every other
    /// reference the node holds (<see cref="IHeapReferenceQuery.EnumerateReferences"/>). Returns the
    /// first object for which <paramref name="isMatch"/> returns <c>true</c>, or <c>false</c> if
    /// <paramref name="maxDepth"/> is exhausted or the graph is fully explored first.
    /// </summary>
    public static bool TryFindByPredicate(
        ulong sourceAddress,
        HashSet<ulong> visited,
        int depth,
        int maxDepth,
        IReadOnlyList<string> prioritizedFieldNames,
        IDumpObjectFieldQuery fields,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery referenceQuery,
        Func<HeapObjectRef, bool> isMatch,
        out HeapObjectRef match)
    {
        match = default;
        if (sourceAddress == 0 || depth > maxDepth || !visited.Add(sourceAddress))
            return false;

        if (!objectLookup.TryGetObject(sourceAddress, out HeapObjectRef source))
            return false;

        if (isMatch(source))
        {
            match = source;
            return true;
        }

        for (int i = 0; i < prioritizedFieldNames.Count; i++)
        {
            if (!fields.TryReadField(sourceAddress, prioritizedFieldNames[i], out FieldValue value)
                || value.Kind != FieldValueKind.ObjectReference)
                continue;

            ulong childAddress = value.AsObjectAddress();
            if (childAddress == 0)
                continue;

            if (TryFindByPredicate(childAddress, visited, depth + 1, maxDepth, prioritizedFieldNames, fields, objectLookup, referenceQuery, isMatch, out match))
                return true;
        }

        foreach (ulong childAddress in referenceQuery.EnumerateReferences(sourceAddress))
        {
            if (childAddress == 0)
                continue;

            if (TryFindByPredicate(childAddress, visited, depth + 1, maxDepth, prioritizedFieldNames, fields, objectLookup, referenceQuery, isMatch, out match))
                return true;
        }

        return false;
    }
}
