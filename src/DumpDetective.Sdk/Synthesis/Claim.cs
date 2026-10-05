using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// A synthesized claim over one or more observations (named <c>Finding</c> until M1 step 3), produced by an
/// <see cref="ISynthesisRule"/> — not authored per-analyzer. <see cref="DerivedFrom"/> is the field
/// that matters most: it traces a finding to the exact observations that produced it, and each of
/// those traces to the artifact + analyzer + capabilities that produced it in turn. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 2.
/// </summary>
public sealed record Claim
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

    /// <summary>
    /// The structured statement this finding makes. Null until a rule has been migrated to author one
    /// (docs/ReportStructure/m1-claims-plan.md step 4); becomes <c>required</c> once the last rule has.
    /// </summary>
    public Assertion? Assertion { get; init; }

    public IReadOnlyList<EvidenceStatement> Support { get; init; } = [];

    /// <summary>Null means counter-evidence was never examined; <see cref="Synthesis.Counter.None"/> means none was found.</summary>
    public Counter? Counter { get; init; }

    public IReadOnlyList<FalsificationTest> FalsifiedBy { get; init; } = [];

    /// <summary>Stamped by <c>SynthesisEngine</c> from the rule that returned this finding.</summary>
    public RuleRef? Rule { get; init; }

    /// <summary>
    /// Mirror <c>Core.Models.InsightFinding</c>'s <c>Analyzer</c>/<c>Category</c>/<c>Tags</c>/
    /// <c>MetricValue</c>/<c>MetricUnit</c> — added 2026-09-18 during Phase 5 step 2's first real
    /// rule migration (<c>ArraySynthesisRule</c>), not speculatively: the report pipeline reads all
    /// five today (<c>ActionPriorityService</c>/<c>CorrelationBuilder</c> group and score by
    /// <c>Category</c>/<c>Tags</c>; several section builders render <c>MetricValue</c>/<c>MetricUnit</c>
    /// on charts), so a rule-produced <see cref="Claim"/> with nowhere to carry them couldn't
    /// actually replace an <c>IFindingGenerator</c> in the real report, only in an isolated test.
    /// Unlike <see cref="Assertion"/>, <see cref="Support"/>, <see cref="Counter"/> and
    /// <see cref="FalsifiedBy"/>, these are not forward-looking: they're what today's pipeline already requires.
    /// </summary>
    public required string Analyzer { get; init; }

    public required string Category { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public double? MetricValue { get; init; }

    public string? MetricUnit { get; init; }
}
