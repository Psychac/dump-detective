using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// Whether a growing value is bad, good, or not classifiable in isolation for a given
/// <c>(ObservationType, measure key)</c> pair. Deliberately not a field on <see cref="Measure"/>
/// itself — direction is domain knowledge about what a measure *means*, declared once per key in
/// <c>schema/DumpDetective.Schema/observation-type-registry.json</c> (next to that key's
/// <c>unit</c>/<c>semantics</c>), not per-instance data a producing analyzer emits. See
/// docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeasureDirection>))]
public enum MeasureDirection
{
    HigherIsWorse,
    LowerIsWorse,
    Neutral,

    /// <summary>"Worse" depends on context the measure alone can't express (e.g. thread count).
    /// Declared explicitly rather than left absent, so a missing <c>direction</c> key in the
    /// registry means "not yet classified," never "silently defaulted to neutral."</summary>
    ContextDependent,
}
