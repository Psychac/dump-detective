namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Context handed to a rule alongside its matched observations.
/// </summary>
/// <remarks>
/// Deliberately minimal for this trimmed Phase 1 pass — a session-scoped concept (carrying e.g. a
/// session id, or a handle back to the full observation store for a rule that needs to look beyond
/// its own match set) is a Phase 4/5 concern, not yet built. Grows as those phases land rather than
/// being speculatively shaped now.
/// </remarks>
public sealed record SynthesisContext
{
    /// <summary>
    /// How many analyzers ran, and how many of those failed — pipeline metadata, not a fact derived
    /// from the artifact, so it deliberately doesn't go through <see cref="Observations.Observation"/>
    /// (which exists to carry claims about the dump/trace, not about the tool's own execution). Just
    /// the two counts, not the full run-result list: <c>DumpDetective.Core.Models.AnalyzerRunResult</c>
    /// is <c>internal</c> to a different assembly the SDK must not depend on, and a count is all
    /// <c>AnalyzerFailuresInsightRule</c> — the one rule needing this — actually reads. Added Phase 5
    /// step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md), not speculatively
    /// wired in ahead of a real need. Null when the caller doesn't have run results to hand (e.g.
    /// most unit tests).
    /// </summary>
    public (int Total, int Failed)? AnalyzerRunSummary { get; init; }
}
