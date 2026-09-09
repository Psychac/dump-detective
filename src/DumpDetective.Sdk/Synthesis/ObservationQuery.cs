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

    /// <summary>Optional additional filter beyond type — deliberately a delegate for now rather
    /// than a declarative predicate structure, since that structure isn't designed yet.</summary>
    public Func<Observation, bool>? AdditionalPredicate { get; init; }

    public bool Matches(Observation observation) =>
        ObservationTypes.Contains(observation.ObservationType, StringComparer.Ordinal)
        && (AdditionalPredicate is null || AdditionalPredicate(observation));
}
