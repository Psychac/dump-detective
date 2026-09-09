namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Confidence as a scored breakdown rather than one opaque number, so a reader can see *why* a
/// finding is only 40% confident and go fix the input. <see cref="IdentityFidelityCap"/> and
/// <see cref="TemporalAlignmentCap"/> are the load-bearing fields: no amount of corroborating
/// evidence can push confidence above the identity-join fidelity or temporal-alignment confidence
/// underlying the finding. See docs/refactor/modularity/observation-and-correlation-model.md § 4.
/// </summary>
public sealed record ConfidenceBreakdown(
    double Composite,
    double EvidenceStrength,
    double IdentityFidelityCap,
    double TemporalAlignmentCap,
    double CapabilityFidelity,
    double ConflictPenalty,
    IReadOnlyList<string> LimitingFactors);
