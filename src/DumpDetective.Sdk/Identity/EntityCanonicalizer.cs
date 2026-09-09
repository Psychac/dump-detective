using System.Text.RegularExpressions;

namespace DumpDetective.Sdk.Identity;

/// <summary>
/// Normalizes type/method names from different sources (ClrMD heap-live names, TraceEvent
/// method-declaring-type names) onto a shared canonical form, per the rules table in
/// docs/refactor/modularity/source-model.md § 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope of this first pass, stated honestly.</b> The ordinal-stripping technique here for
/// compiler-generated names (<c>DisplayClass</c>, <c>&gt;d__</c>, <c>&gt;b__</c>) is not a fresh
/// guess — it is exactly the technique measured in <c>tools/EntityJoinSpike/Program.cs</c> against
/// two real dump+ETL pairs, where it recovered real additional matches (see
/// docs/refactor/modularity-plan.md § "Entity-join spike — measured, 2026-09-08"). The local
/// function rule generalizes the same proven technique (strip a build-order-dependent numeric
/// ordinal) rather than inventing new unproven heuristics. Per Phase 1's own risk assessment, this
/// component is meant to become "its own reviewed, test-heavy deliverable" once Phase 6a's larger
/// cross-source corpus exists — this implementation is deliberately conservative until then: where
/// the source-model.md table calls for full semantic unwrapping this pass doesn't yet attempt
/// (e.g. recovering a lambda's enclosing method name from a bare <c>DisplayClass</c> class, which
/// generally isn't recoverable from the name alone), it falls back to the fidelity tier the table
/// assigns for that case rather than guessing.
/// </para>
/// <para>
/// <b>Known, un-handled gap: dynamic/reflection-emitted types (table row "Dynamic/reflection-emitted
/// → None").</b> There is no reliable naming convention to detect these from the type name string
/// alone — detecting them needs artifact-side module info (e.g. a dynamic assembly flag), which
/// this name-only canonicalizer doesn't have access to. Names that don't match any recognized
/// compiler-generated pattern currently fall through to <see cref="MatchFidelity.Exact"/>, which is
/// wrong for a genuinely dynamic type, not merely conservative. Left as an open gap rather than a
/// guessed heuristic; a caller with module-level information should override the fidelity for types
/// it knows came from a dynamic module.
/// </para>
/// </remarks>
public static class EntityCanonicalizer
{
    private static readonly Regex DisplayClassOrdinal = new(@"(DisplayClass)\d+(_\d+)?", RegexOptions.Compiled);
    private static readonly Regex AsyncStateMachineOrdinal = new(@"(>d__)\d+", RegexOptions.Compiled);
    private static readonly Regex LambdaMethodOrdinal = new(@"(>b__)\d+", RegexOptions.Compiled);
    private static readonly Regex LocalFunctionOrdinal = new(@"(>g__[A-Za-z_][A-Za-z0-9_]*)\|\d+(_\d+)?", RegexOptions.Compiled);
    private static readonly Regex AsyncStateMachineUnwrap = new(@"<(?<method>[^>]+)>d__\d+", RegexOptions.Compiled);

    /// <summary>
    /// Canonicalizes a type name (as reported by ClrMD or TraceEvent) into a join-comparable form
    /// and the fidelity that join deserves.
    /// </summary>
    public static (string CanonicalName, MatchFidelity Fidelity) CanonicalizeTypeName(string rawName)
    {
        ArgumentNullException.ThrowIfNull(rawName);

        string stripped = StripAssemblyQualification(rawName);

        if (AsyncStateMachineUnwrap.Match(stripped) is { Success: true } unwrapMatch)
        {
            // Async state machine: unwrap to the declaring method name. The compiler-assigned
            // ordinal is compile-order-dependent and deliberately excluded from the join key.
            return (unwrapMatch.Groups["method"].Value, MatchFidelity.High);
        }

        if (LocalFunctionOrdinal.IsMatch(stripped))
        {
            return (LocalFunctionOrdinal.Replace(stripped, "$1"), MatchFidelity.Medium);
        }

        if (DisplayClassOrdinal.IsMatch(stripped) || stripped.Contains("<>c", StringComparison.Ordinal))
        {
            string canonical = DisplayClassOrdinal.Replace(stripped, "$1");
            canonical = LambdaMethodOrdinal.Replace(canonical, "$1");
            return (canonical, MatchFidelity.Low);
        }

        if (LambdaMethodOrdinal.IsMatch(stripped))
        {
            return (LambdaMethodOrdinal.Replace(stripped, "$1"), MatchFidelity.Low);
        }

        // Not a recognized compiler-generated shape: simple types, generics, arrays, pointers,
        // byref — assembly-qualification stripping alone is sufficient for these to join exactly.
        return (stripped, MatchFidelity.Exact);
    }

    /// <summary>
    /// Normalizes a parameter type list into the signature suffix used by
    /// <see cref="MethodRef.NormalizedSignature"/>, e.g. <c>(System.String,System.Int32)</c>.
    /// Each parameter type is canonicalized independently via <see cref="CanonicalizeTypeName"/>;
    /// the returned fidelity is the minimum across all parameters (a method's overall join
    /// trustworthiness can't exceed its least-trustworthy parameter type).
    /// </summary>
    public static (string NormalizedSignature, MatchFidelity Fidelity) NormalizeSignature(IReadOnlyList<string> parameterTypeNames)
    {
        ArgumentNullException.ThrowIfNull(parameterTypeNames);

        if (parameterTypeNames.Count == 0)
        {
            return ("()", MatchFidelity.Exact);
        }

        var canonicalParams = new string[parameterTypeNames.Count];
        MatchFidelity worst = MatchFidelity.Exact;

        for (int i = 0; i < parameterTypeNames.Count; i++)
        {
            (string canonical, MatchFidelity fidelity) = CanonicalizeTypeName(parameterTypeNames[i]);
            canonicalParams[i] = canonical;
            if (fidelity < worst)
            {
                worst = fidelity;
            }
        }

        return ($"({string.Join(",", canonicalParams)})", worst);
    }

    // Matches the standard Type.AssemblyQualifiedName suffix shape: ", AssemblyName, Version=X.X.X.X"
    // with optional trailing ", Culture=..." / ", PublicKeyToken=...". Anchored on the mandatory
    // "AssemblyName, Version=" pair (always present in a real AssemblyQualifiedName) so this can't
    // accidentally match an unrelated comma. A single global replace strips every occurrence
    // regardless of nesting depth inside generic-argument brackets — e.g.
    // "List`1[[System.String, mscorlib, Version=4.0.0.0, ...]], mscorlib, Version=4.0.0.0, ..." —
    // without needing to track bracket depth at all, which a manual walk got wrong for anything
    // beyond one level of nesting.
    private static readonly Regex AssemblyQualificationSuffix = new(
        @",\s*[A-Za-z_][A-Za-z0-9_.]*,\s*Version=\d+(?:\.\d+){0,3}(?:,\s*Culture=(?:neutral|[A-Za-z-]+))?(?:,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Strips assembly-qualification (<c>, AssemblyName, Version=..., Culture=..., PublicKeyToken=...</c>)
    /// from a type name so <c>"System.String, mscorlib, Version=4.0.0.0, ..."</c> and a trace-side
    /// <c>"System.String"</c> compare equal.
    /// </summary>
    private static string StripAssemblyQualification(string name) =>
        AssemblyQualificationSuffix.Replace(name, string.Empty);
}
