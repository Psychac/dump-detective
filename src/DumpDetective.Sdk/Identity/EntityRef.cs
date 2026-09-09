namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A cross-source-comparable reference to an entity (type, method, module, thread, or object).
/// <see cref="JoinKey"/> is the canonical identity used for correlation; concrete subtypes also
/// carry source-local handles (e.g. a dump's <c>MethodTable</c>) for drill-down, but those are
/// never part of the join. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
public abstract record EntityRef
{
    public abstract EntityKind Kind { get; }

    /// <summary>Canonical, cross-source-comparable identity. Never a source-local handle.</summary>
    public abstract string JoinKey { get; }

    /// <summary>How trustworthy <see cref="JoinKey"/> is for this specific entity.</summary>
    public required MatchFidelity Fidelity { get; init; }
}
