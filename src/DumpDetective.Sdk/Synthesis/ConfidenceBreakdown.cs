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

    /// <summary>
    /// Every cap and factor at its ceiling — what a rule returns when nothing about its own evidence
    /// limits the claim. The observation lineage's measured limits are folded in afterwards by
    /// <see cref="ConfidenceCalculator"/>, so a rule never re-derives them itself.
    /// </summary>
    public static ConfidenceBreakdown MeasurementCertain { get; } = new()
    {
        Composite = 1.0,
        EvidenceStrength = 1.0,
        IdentityFidelityCap = 1.0,
        TemporalAlignmentCap = 1.0,
        CapabilityFidelity = 1.0,
        ConflictPenalty = 1.0,
    };
}
