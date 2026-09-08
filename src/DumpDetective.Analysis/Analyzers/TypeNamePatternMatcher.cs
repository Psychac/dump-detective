namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Shared primitives for the namespace-prefix / suffix / contains-token type-name matching
/// pattern independently reimplemented by several analyzers (<c>DbConnectionAnalyzer</c>,
/// <c>WcfChannelAnalyzer</c>, <c>HttpObjectAnalyzer</c>, <c>TimerLeakAnalyzer</c>,
/// <c>CollectionAnalyzer</c>, <c>AsyncTaskAnalyzer</c>). Each caller keeps its own literal
/// prefix/suffix/token lists and category enum; only the matching boilerplate is shared here.
/// </summary>
internal static class TypeNamePatternMatcher
{
    private static readonly char[] TypeNameCutChars = ['`', '[', '<', '+'];

    public static bool HasAnyPrefix(string typeName, string[] prefixes)
    {
        for (int i = 0; i < prefixes.Length; i++)
        {
            if (typeName.StartsWith(prefixes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool ContainsAny(string typeName, string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (typeName.Contains(tokens[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="typeName"/> starts with any of <paramref name="prefixes"/> AND
    /// (ends with <paramref name="requiredSuffix"/> OR contains any of <paramref name="containsAnyTokens"/>).
    /// Either <paramref name="requiredSuffix"/> or <paramref name="containsAnyTokens"/> may be null.
    /// </summary>
    public static bool HasPrefixAndSuffixOrContains(
        string typeName, string[] prefixes, string? requiredSuffix, string[]? containsAnyTokens)
    {
        if (!HasAnyPrefix(typeName, prefixes))
            return false;

        if (requiredSuffix is not null && typeName.EndsWith(requiredSuffix, StringComparison.Ordinal))
            return true;

        if (containsAnyTokens is not null && ContainsAny(typeName, containsAnyTokens))
            return true;

        return false;
    }

    /// <summary>
    /// Strips generic/nested-type noise from a resolved type name, then returns the segment
    /// after the last '.'. E.g. "System.Collections.Generic.Dictionary`2[[...]]" -&gt; "Dictionary".
    /// </summary>
    public static string GetShortName(string typeName)
    {
        string outer = typeName;
        int cut = outer.IndexOfAny(TypeNameCutChars);
        if (cut >= 0) outer = outer.Substring(0, cut);
        int lastDot = outer.LastIndexOf('.');
        return lastDot >= 0 ? outer.Substring(lastDot + 1) : outer;
    }
}
