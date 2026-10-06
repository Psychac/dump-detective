namespace DumpDetective.Reporting.Models;

/// <summary>
/// What a renderer needs to show a claim, as wording rather than ids: the assertion as a sentence, why
/// confidence is limited, and the evidence for and against. Generated once by <c>ClaimProse</c> so every
/// format says the same thing. Observation ids are deliberately absent — the HTML cannot resolve them, and
/// citing them there more than doubled a real report's size; they stay in <c>report.json</c> and the side-car.
/// </summary>
internal sealed record ClaimView(
    string Predicate,
    string Assertion,
    double? Confidence,
    string? ConfidenceBand,
    string? ConfidenceReason,
    IReadOnlyList<ClaimEvidenceView> Support,
    ClaimCounterView? Counter,
    IReadOnlyList<ClaimTestView> Tests,
    ClaimBreakdownView? Breakdown);

/// <param name="Observations">How many observations the statement cites.</param>
/// <param name="Sources">Analyzers that produced those observations; the HTML links each to its section.</param>
internal sealed record ClaimEvidenceView(string Why, int Observations, IReadOnlyList<string> Sources);

/// <param name="NoneBecause">Set instead of statements when counter-evidence was examined and none exists.</param>
internal sealed record ClaimCounterView(IReadOnlyList<ClaimEvidenceView> Statements, string? NoneBecause);

internal sealed record ClaimTestView(string Kind, string Test, string? Command);

internal sealed record ClaimBreakdownView(
    double Composite,
    double EvidenceStrength,
    double IdentityFidelityCap,
    double TemporalAlignmentCap,
    double CapabilityFidelity,
    double ConflictPenalty,
    IReadOnlyList<string> LimitingFactors);

/// <summary>Analyzers behind each support / counter statement, parallel to <c>Support</c> and <c>Counter.Statements</c>.</summary>
internal sealed record EvidenceSources(
    IReadOnlyList<IReadOnlyList<string>> Support,
    IReadOnlyList<IReadOnlyList<string>> Counter);
