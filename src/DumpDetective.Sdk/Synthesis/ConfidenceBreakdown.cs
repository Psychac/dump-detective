namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Confidence as a scored breakdown rather than one opaque number, so a reader can see *why* a
/// finding is only 40% confident and go fix the input. <see cref="IdentityFidelityCap"/> and
/// <see cref="TemporalAlignmentCap"/> are the load-bearing fields: no amount of corroborating
/// evidence can push confidence above the identity-join fidelity or temporal-alignment confidence
/// underlying the finding. See docs/refactor/modularity/observation-and-correlation-model.md § 4.
/// </summary>
/// <remarks>
/// Named `required` properties, not positional construction — six adjacent same-typed (`double`)
/// fields were silently transposable at a positional call site with zero compiler protection. See
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 5.
/// </remarks>
public sealed record ConfidenceBreakdown
{
    public required double Composite { get; init; }
    public required double EvidenceStrength { get; init; }
    public required double IdentityFidelityCap { get; init; }
    public required double TemporalAlignmentCap { get; init; }
    public required double CapabilityFidelity { get; init; }
    public required double ConflictPenalty { get; init; }
    public IReadOnlyList<string> LimitingFactors { get; init; } = [];
}
