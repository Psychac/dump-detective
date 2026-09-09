using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A cross-source-comparable reference to an entity (type, method, module, thread, or object).
/// <see cref="JoinKey"/> is the canonical identity used for correlation; concrete subtypes also
/// carry source-local handles (e.g. a dump's <c>MethodTable</c>) for drill-down, but those are
/// never part of the join. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
/// <remarks>
/// Polymorphic JSON attributes matter beyond cosmetics here: <see cref="Observations.Observation.Subjects"/>
/// is declared <c>IReadOnlyList&lt;EntityRef&gt;</c>, so without <see cref="JsonDerivedTypeAttribute"/>
/// telling <c>System.Text.Json</c> the concrete subtypes, serializing a <see cref="MethodRef"/> or
/// <see cref="ThreadRef"/> through that property would silently emit only <see cref="EntityRef"/>'s
/// own members (<see cref="Kind"/>, <see cref="JoinKey"/>, <see cref="Fidelity"/>) and drop every
/// subtype-specific field — the method name, the thread id, everything that makes the JSON useful.
/// Tag values match <see cref="EntityKind"/>'s own names, lowercased. Discriminator property is
/// named <c>$kind</c>, not <c>kind</c> — <see cref="Kind"/> is itself a real serialized property
/// that would collide with the discriminator name in camelCase, the same reason
/// <c>AnalysisReportDocument</c> (Reporting project) already uses <c>$kind</c> for its own
/// polymorphic base.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TypeRef), "type")]
[JsonDerivedType(typeof(MethodRef), "method")]
[JsonDerivedType(typeof(ModuleRef), "module")]
[JsonDerivedType(typeof(ThreadRef), "thread")]
[JsonDerivedType(typeof(ObjectRef), "object")]
public abstract record EntityRef
{
    public abstract EntityKind Kind { get; }

    /// <summary>Canonical, cross-source-comparable identity. Never a source-local handle.</summary>
    public abstract string JoinKey { get; }

    /// <summary>How trustworthy <see cref="JoinKey"/> is for this specific entity.</summary>
    public required MatchFidelity Fidelity { get; init; }
}
