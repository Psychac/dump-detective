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
/// Ordered worst-to-best is deliberately avoided as an assumption here — callers that need to
/// compare two fidelities (e.g. take the minimum across a join lineage) should do so explicitly
/// against the ranking documented in
/// docs/refactor/modularity/source-model.md § 4's canonicalization-rules table:
/// <see cref="None"/> &lt; <see cref="Low"/> &lt; <see cref="Medium"/> &lt; <see cref="High"/> &lt;
/// <see cref="Exact"/>.
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
