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
IReadOnlyList<string>? Caveats = null)
{
    public double? ConfidenceScore { get; init; } = ConfidenceScore ?? Severity switch
    {
        FindingSeverity.Critical => 0.9,
        FindingSeverity.Warning => 0.7,
        _ => 0.5
    };

    public IReadOnlyList<string> EffectiveCaveats { get; init; } = Caveats ?? Array.Empty<string>();

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
