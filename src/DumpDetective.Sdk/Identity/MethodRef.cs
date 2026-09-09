namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A managed method. <see cref="JoinKey"/> combines the declaring type's join key, the method
/// name, and a normalized parameter signature — overload-distinguishing, but never including a
/// compiler-assigned ordinal. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
public sealed record MethodRef : EntityRef
{
    public override EntityKind Kind => EntityKind.Method;

    public required TypeRef DeclaringType { get; init; }

    public required string Name { get; init; }

    /// <summary>Canonicalized parameter type list, e.g. "(System.String,System.Int32)".</summary>
    public required string NormalizedSignature { get; init; }

    /// <summary>Dump-local handle — not part of <see cref="JoinKey"/>.</summary>
    public ulong? MethodDesc { get; init; }

    /// <summary>Trace-local handle — not part of <see cref="JoinKey"/>.</summary>
    public int? MethodToken { get; init; }

    public override string JoinKey => $"{DeclaringType.JoinKey}::{Name}{NormalizedSignature}";
}
