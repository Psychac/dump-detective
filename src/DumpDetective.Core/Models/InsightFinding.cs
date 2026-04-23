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
string? MetricUnit = null)
{
public string EffectiveFingerprint =>
    !string.IsNullOrWhiteSpace(Fingerprint)
        ? Fingerprint
        : FindingFingerprint.Build(Analyzer, Category, Title, Tags);
}
