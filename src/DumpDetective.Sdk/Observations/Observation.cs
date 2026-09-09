using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// A typed, entity-anchored, time-extented, evidence-bearing quantitative fact — an analyzer's
/// primary output. Enough structure to diff, join, rank, and fuse generically, which is what
/// unlocks cross-source correlation and a single generic trend differ. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 1.
/// </summary>
/// <remarks>
/// Purity rules (§ 2a of the same doc) — enforced by convention here, not by the type system:
/// <see cref="Measures"/> must be raw only (never a weighted composite or a value normalized
/// against a hand-picked constant); <see cref="ObservationType"/> must be a factual
/// characterization (e.g. <c>"gc.generation-composition"</c>), never a severity claim (never
/// <c>"gc.pressure-high"</c>); <see cref="Confidence"/> is *measurement* confidence only (was a
/// capability degraded, was sampling partial), never severity confidence. Severity, banding, and
/// weighting are synthesis-rule outputs, not observation fields.
/// </remarks>
public sealed record Observation
{
    public required ObservationId Id { get; init; }

    public required string ObservationType { get; init; }

    public required IReadOnlyList<EntityRef> Subjects { get; init; }

    public required TemporalExtent When { get; init; }

    public required IReadOnlyDictionary<string, Measure> Measures { get; init; }

    public required Provenance Provenance { get; init; }

    /// <summary>Measurement confidence only, 0..1 — see the purity-rules remark above.</summary>
    public required double Confidence { get; init; }

    public IReadOnlyList<EvidenceRef> Evidence { get; init; } = [];
}
