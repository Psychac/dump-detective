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

    ObservationQuery Match { get; }

    ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default);
}
