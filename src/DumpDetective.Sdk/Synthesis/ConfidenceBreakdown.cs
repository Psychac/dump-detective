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
    /// Every cap and factor at its ceiling — a dump-only session's default until Phase 7 actually
    /// wires cross-artifact caps in. Removes the 30-rule copy-paste-the-same-1.0-object boilerplate
    /// this record would otherwise force on every Phase 5 step-2 synthesis rule; see
    /// docs/refactor/modularity/phase-5-observations-synthesis.md's "Before step 2" prerequisites.
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
