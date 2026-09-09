namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A managed type. <see cref="JoinKey"/> is <see cref="CanonicalName"/> — produced by
/// <see cref="EntityCanonicalizer"/>, never computed ad hoc by callers, so every caller agrees on
/// the same canonical form. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
public sealed record TypeRef : EntityRef
{
    public override EntityKind Kind => EntityKind.Type;

    /// <summary>Canonicalized, assembly-qualification/version/culture/token-stripped name.</summary>
    public required string CanonicalName { get; init; }

    /// <summary>Dump-local handle — not part of <see cref="JoinKey"/>.</summary>
    public ulong? MethodTable { get; init; }

    /// <summary>Trace-local handle — not part of <see cref="JoinKey"/>.</summary>
    public int? TypeToken { get; init; }

    public ModuleRef? Module { get; init; }

    public override string JoinKey => CanonicalName;
}
