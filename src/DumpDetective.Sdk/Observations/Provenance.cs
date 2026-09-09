using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// Where an <see cref="Observation"/> came from: which artifact, which analyzer, and which
/// capabilities that analyzer actually used to produce it. This is what makes "why does the tool
/// think this?" answerable end to end, and what lets confidence scoring distinguish a full-fidelity
/// run from a degraded one. See docs/refactor/modularity/observation-and-correlation-model.md § 1, § 4.
/// </summary>
public sealed record Provenance
{
    public required ArtifactId Artifact { get; init; }

    public required string AnalyzerKey { get; init; }

    public required IReadOnlySet<Capability> CapabilitiesUsed { get; init; }

    public required FidelityLevel Fidelity { get; init; }
}
