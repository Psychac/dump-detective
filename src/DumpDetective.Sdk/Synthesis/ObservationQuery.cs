using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Declares which observations an <see cref="ISynthesisRule"/> matches.
/// </summary>
/// <remarks>
/// <b>First-cut shape, not the final design.</b> Whether synthesis rules should match via a real
/// declarative grammar (a DSL/config, tunable without a rebuild) or stay code-driven is an open
/// question — see docs/refactor/modularity/observation-and-correlation-model.md § 7, currently
/// leaning toward this hybrid: rules themselves in code, matching declarative. This type is the
/// minimal matcher needed to unblock Phase 6b's first rules, not a committed grammar; expect it to
/// change once real rule authoring surfaces what the grammar actually needs to express.
/// </remarks>
public sealed record ObservationQuery
{
    /// <summary>Matches if the observation's <c>ObservationType</c> is any of these.</summary>
    public required IReadOnlyList<string> ObservationTypes { get; init; }

    /// <summary>
    /// Optional additional filter beyond type — deliberately a delegate for now rather than a
    /// declarative predicate structure, since that structure isn't designed yet.
    /// </summary>
    /// <remarks>
    /// Concrete, not just theoretical, consequence of staying a delegate — considered and left
    /// as-is 2026-09-10 (docs/refactor/modularity/phase-1-sdk-review-findings.md item 16): a
    /// <see cref="Func{T,TResult}"/> cannot be inspected/validated at plugin-discovery time (Phase
    /// 3 can check a rule's declared <c>RuleId</c> or capability requirements without running code,
    /// but never what this delegate actually tests for), and it cannot cross an
    /// <c>AssemblyLoadContext</c> boundary (Phase 9) — a drop-in plugin rule with a non-trivial
    /// <see cref="AdditionalPredicate"/> cannot be isolated the way Phase 9 isolates everything
    /// else. Not fixed here: there is no real synthesis rule anywhere in the codebase yet to design
    /// a declarative replacement against (`ISynthesisRule` has zero implementations), and guessing
    /// at a grammar without one risks building the wrong one. Left exactly as first-cut, with the
    /// real cost named plainly so Phase 5's rule authoring and Phase 9's isolation work hit this
    /// directly, not as a surprise.
    /// </remarks>
    public Func<Observation, bool>? AdditionalPredicate { get; init; }

    public bool Matches(Observation observation) =>
        ObservationTypes.Contains(observation.ObservationType, StringComparer.Ordinal)
        && (AdditionalPredicate is null || AdditionalPredicate(observation));
}
