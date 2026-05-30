using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Traversal;

internal static class ObjectGraphTraversal
{
    public static bool TryFindByPredicate(
        ClrObject source,
        HashSet<ulong> visited,
        int depth,
        int maxDepth,
        IReadOnlyList<string> prioritizedFieldNames,
        Func<ClrObject, bool> isMatch,
        Func<ClrObject, string, ClrObject> readObjectField,
        out ClrObject match)
    {
        match = default;
        if (!source.IsValid || source.Address == 0 || depth > maxDepth || !visited.Add(source.Address))
            return false;

        if (isMatch(source))
        {
            match = source;
            return true;
        }

        if (source.Type is null)
            return false;

        for (int i = 0; i < prioritizedFieldNames.Count; i++)
        {
            ClrObject child = readObjectField(source, prioritizedFieldNames[i]);
            if (!child.IsValid)
                continue;

            if (TryFindByPredicate(child, visited, depth + 1, maxDepth, prioritizedFieldNames, isMatch, readObjectField, out match))
                return true;
        }

        foreach (ClrObject child in source.EnumerateReferences(carefully: true))
        {
            if (!child.IsValid || child.Address == 0)
                continue;

            if (TryFindByPredicate(child, visited, depth + 1, maxDepth, prioritizedFieldNames, isMatch, readObjectField, out match))
                return true;
        }

        return false;
    }
}