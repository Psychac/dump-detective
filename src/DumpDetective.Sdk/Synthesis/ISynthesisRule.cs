namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Replaces per-analyzer <c>IFindingGenerator</c> code with expressed rules over a uniform
/// substrate. Three tiers use this same mechanism: single-observation rules (most of today's
/// per-analyzer finding generators), multi-observation single-source rules (what
/// <c>InsightEngine</c> does today, generalized), and cross-source rules (Phase 7) — structurally
/// identical, just matching observations whose <c>Provenance.Artifact</c> differs. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 3.
/// </summary>
public interface ISynthesisRule
{
    string RuleId { get; }

    /// <summary>Named <c>Query</c>, not <c>Match</c> — renamed 2026-09-10
    /// (docs/refactor/modularity/phase-1-sdk-review-findings.md item 19) to remove the collision
    /// with <see cref="ObservationQuery.Matches"/> sitting right next to it
    /// (<c>rule.Match.Matches(observation)</c> read awkwardly); named after its own type instead.</summary>
    ObservationQuery Query { get; }

    ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default);
}
