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

    /// <summary>
    /// Identity equality — <see cref="Kind"/> + <see cref="JoinKey"/> only. Deliberately excludes
    /// every source-local handle (a dump's <c>MethodTable</c>, a trace's <c>TypeToken</c>, ...) and
    /// <see cref="Fidelity"/> itself: per source-model.md § 4, "JoinKey is the canonical
    /// cross-source identity; [handles] are carried along for drill-down but never used for
    /// joining," and Fidelity is a trust rating *of* the identity, not part of it — two refs to the
    /// same entity resolved with different fidelity are still the same entity. Without this
    /// override, default record equality compares every field, so a dump-side and trace-side
    /// <see cref="TypeRef"/> for the exact same type are never <c>==</c> (their <c>MethodTable</c>/
    /// <c>TypeToken</c> are never both populated), which defeats the one thing <c>EntityRef</c>
    /// exists to make possible: <c>Dictionary&lt;EntityRef,_&gt;</c>/<c>GroupBy</c>/<c>Distinct</c>
    /// finding matching entities across sources. See
    /// docs/refactor/modularity/phase-1-sdk-review-findings.md item 3.
    /// </summary>
    /// <remarks>
    /// Every sealed subtype must re-declare this exact override (records generate a separate typed
    /// <c>Equals</c>/<c>GetHashCode</c> pair at each level of the hierarchy; a derived record does
    /// not inherit a base's override as its own), delegating back to this implementation so the
    /// actual comparison logic lives in exactly one place. See each subtype's own two-line override
    /// for the pattern.
    /// </remarks>
    public virtual bool Equals(EntityRef? other) => other is not null && Kind == other.Kind && JoinKey == other.JoinKey;

    public override int GetHashCode() => HashCode.Combine(Kind, JoinKey);
}
