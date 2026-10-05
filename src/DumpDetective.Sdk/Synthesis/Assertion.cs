using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// What a claim states, as structure rather than prose. Every renderer generates its wording from this, so no
/// two renderers can disagree and a test can assert the claim instead of a sentence.
/// </summary>
public sealed record Assertion
{
    public required AssertionPredicate Predicate { get; init; }

    /// <summary>Null for process-wide statements that are about no single entity.</summary>
    public EntityRef? Subject { get; init; }

    public Measure? Measure { get; init; }

    public Reference? Reference { get; init; }

    public AssertionQualifiers Qualifiers { get; init; } = new();
}

public sealed record AssertionQualifiers
{
    public string? Mechanism { get; init; }

    public string? Location { get; init; }

    public string? Scope { get; init; }
}

/// <summary>
/// What a measure is compared against. Only a budget exists today — the rule's own threshold, so that
/// <see cref="AssertionPredicate.Exceeds"/> can say what it exceeded. Prior snapshot, rate and distribution
/// arrive with the baselines work (vision § 8).
/// </summary>
public sealed record Reference
{
    public required Measure Value { get; init; }

    /// <summary>Where the number comes from, e.g. "LOH share warning threshold".</summary>
    public required string Basis { get; init; }
}
