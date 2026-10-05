using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>Observations that bear on a claim, with the rule author's one-line reason they do.</summary>
public sealed record EvidenceStatement(IReadOnlyList<ObservationId> Observations, string Why);

/// <summary>
/// Evidence that weakens a claim. A claim with no <see cref="Counter"/> at all was never examined for
/// counter-evidence; <see cref="None"/> says it was examined and nothing was found, which is a different statement.
/// </summary>
public sealed record Counter
{
    public IReadOnlyList<EvidenceStatement> Statements { get; init; } = [];

    /// <summary>Set only when no counter-evidence exists; says what was considered.</summary>
    public string? NoneFoundBecause { get; init; }

    public static Counter Of(params EvidenceStatement[] statements) => new() { Statements = statements };

    public static Counter None(string consideredReason) => new() { NoneFoundBecause = consideredReason };
}
