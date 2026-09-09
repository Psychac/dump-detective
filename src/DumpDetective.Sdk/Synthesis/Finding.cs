using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// A synthesized narrative over one or more observations, produced by an
/// <see cref="ISynthesisRule"/> — not authored per-analyzer. <see cref="DerivedFrom"/> is the field
/// that matters most: it traces a finding to the exact observations that produced it, and each of
/// those traces to the artifact + analyzer + capabilities that produced it in turn. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 2.
/// </summary>
public sealed record Finding
{
    /// <summary>Stable across runs — dedup/trend identity.</summary>
    public required string Fingerprint { get; init; }

    public required Severity Severity { get; init; }

    public required string Title { get; init; }

    public required string Narrative { get; init; }

    public required string Recommendation { get; init; }

    /// <summary>Full observation lineage — never empty for a rule-produced finding.</summary>
    public required IReadOnlyList<ObservationId> DerivedFrom { get; init; }

    public required ConfidenceBreakdown Confidence { get; init; }

    public IReadOnlyList<string> Caveats { get; init; } = [];
}
