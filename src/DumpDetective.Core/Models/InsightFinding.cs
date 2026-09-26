using DumpDetective.Core.Enums;

namespace DumpDetective.Core.Models;

public sealed record InsightFinding(
string Analyzer,
string Category,
FindingSeverity Severity,
string Title,
string Evidence,
string Recommendation,
IReadOnlyList<string> Tags,
string? Fingerprint = null,
double? MetricValue = null,
string? MetricUnit = null,
double? ConfidenceScore = null,
IReadOnlyList<string>? Caveats = null,
IReadOnlyList<FindingEvidenceTable>? EvidenceTables = null)
{
    public double? ConfidenceScore { get; init; } = ConfidenceScore ?? Severity switch
    {
        FindingSeverity.Critical => 0.9,
        FindingSeverity.Warning => 0.7,
        _ => 0.5
    };

    public IReadOnlyList<string> EffectiveCaveats { get; init; } = Caveats ?? Array.Empty<string>();

    /// <summary>What measurably lowered <see cref="ConfidenceScore"/> (a capped scan, sampling, an
    /// estimated size) — empty when the score is the severity-based default. The report derives a
    /// section's confidence badge from these, so the badge and its findings can't disagree.</summary>
    public IReadOnlyList<string> ConfidenceLimitingFactors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FindingEvidenceTable> EffectiveEvidenceTables { get; init; } = EvidenceTables ?? Array.Empty<FindingEvidenceTable>();

    public double EffectiveConfidenceScore => ConfidenceScore ?? Severity switch
    {
        FindingSeverity.Critical => 0.9,
        FindingSeverity.Warning => 0.7,
        _ => 0.5
    };

    public string EffectiveFingerprint =>
        !string.IsNullOrWhiteSpace(Fingerprint)
            ? Fingerprint
            : FindingFingerprint.Build(Analyzer, Category, Title, Tags);
}
