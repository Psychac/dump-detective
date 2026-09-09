using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Identity;

/// <summary>
/// How trustworthy an <see cref="EntityRef.JoinKey"/> is for cross-source correlation. Not
/// decoration — this caps the confidence of any finding derived from a join on this entity (see
/// docs/refactor/modularity/observation-and-correlation-model.md § 4). A correlation resting on a
/// <see cref="Low"/>-fidelity lambda match cannot be reported as high-confidence, no matter how
/// strongly the two observations agree.
/// </summary>
/// <remarks>
/// <b>Declaration order is the ranking, guaranteed.</b> <see cref="None"/> &lt; <see cref="Low"/>
/// &lt; <see cref="Medium"/> &lt; <see cref="High"/> &lt; <see cref="Exact"/> — this is the exact
/// ranking documented in docs/refactor/modularity/source-model.md § 4's canonicalization-rules
/// table, and ordinal comparison (<c>&lt;</c>/<c>&gt;</c>) against it is safe and intended, not an
/// assumption to avoid. Pinned by
/// <c>tests/DumpDetective.Tests/Unit/Sdk/IdentityTests.cs</c>'s
/// <c>MatchFidelity_DeclarationOrderMatchesDocumentedRanking</c> — inserting or reordering a member
/// anywhere but the correct rank position is a breaking change and must update that test. Prefer
/// <see cref="MatchFidelityExtensions.Min"/> over inline comparisons for "take the weaker of two
/// fidelities" (the exact recurring need once cross-source correlation code exists) so this
/// guarantee has one central, named consumer instead of being re-derived ad hoc at every call site.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MatchFidelity>))]
public enum MatchFidelity
{
    /// <summary>No stable identity — e.g. dynamic/reflection-emitted types. Never joined.</summary>
    None,

    /// <summary>Ordinals shift between builds; safe to join only within one build (lambdas/closures).</summary>
    Low,

    /// <summary>Recoverable with a caveat (local functions, anonymous types).</summary>
    Medium,

    /// <summary>Unwrapped correctly, but one component (e.g. a compiler-assigned ordinal) is not
    /// itself part of the join (async state machines).</summary>
    High,

    /// <summary>Canonical form matches exactly across sources (simple types, generics, arrays, methods).</summary>
    Exact,
}
