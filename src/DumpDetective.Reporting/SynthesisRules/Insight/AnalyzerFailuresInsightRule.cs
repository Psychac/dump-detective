using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectAnalyzerFailures</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Unlike every other migrated rule, this one reads no <c>Observation</c> at all — an analyzer's own
/// success/failure is pipeline metadata, not a fact about the dump/trace being diagnosed, so it has
/// no natural home in the Observation model (which exists specifically for claims about the
/// artifact). <see cref="Query"/> declares an empty <c>ObservationTypes</c>: <c>SynthesisEngine</c>
/// calls every rule regardless of match count ("no zero-match skip" — see its own remarks), so this
/// still runs every pass. Reads <see cref="SynthesisContext.AnalyzerRunSummary"/> instead — added to
/// the SDK specifically for this rule, a small (total, failed) count pair rather than the full
/// <c>AnalyzerRunResult</c> list, which is <c>internal</c> to a different assembly the SDK must not
/// depend on.
/// </remarks>
internal sealed class AnalyzerFailuresInsightRule : ISynthesisRule
{
    private const int WarningCount = 3;

    public string RuleId => "insight.analyzer-failures";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = [] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (context.AnalyzerRunSummary is not (int total, int failed) || failed < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::analyzer-failures",
                Analyzer = "InsightEngine",
                Category = "Analysis",
                Severity = Severity.Warning,
                Title = $"{failed} analyzer(s) failed — insights may be incomplete",
                Narrative = $"{failed} of {total} analyzers did not complete successfully. " +
                            "Cross-cutting correlations that depend on their results will be absent.",
                Recommendation = "Check the diagnostic output for per-analyzer error messages. " +
                                 "Re-run with --diagnostic flag for detailed error information.",
                Tags = ["analysis-quality", "failed-analyzer"],
                DerivedFrom = [],
                Confidence = new ConfidenceBreakdown
                {
                    Composite = 0.55,
                    EvidenceStrength = 0.55,
                    IdentityFidelityCap = 1.0,
                    TemporalAlignmentCap = 1.0,
                    CapabilityFidelity = 1.0,
                    ConflictPenalty = 1.0,
                    LimitingFactors = ["Derived from a partially complete analyzer set."],
                },
            },
        ]);
    }
}
