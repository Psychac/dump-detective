using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Core.Models;

/// <summary>
/// Builds a <see cref="Claim"/> for a finding that no synthesis rule produced — trend regressions, which come from
/// comparing two snapshots, and hand-built findings in tests. Such a claim has no observation lineage, so
/// <see cref="Claim.DerivedFrom"/> is empty and it states no structured assertion.
/// </summary>
/// <remarks>
/// Confidence defaults to the severity-based score a finding without measured evidence has always carried
/// (Critical 0.9, Warning 0.7, Info 0.5). Keeping it is deliberate: replacing it with full confidence would claim
/// more than a snapshot comparison measures, and changing shown confidence is a separate decision from the
/// claim unification.
/// </remarks>
public static class ClaimFactory
{
    public static Claim Create(
        string analyzer,
        string category,
        Severity severity,
        string title,
        string narrative,
        string recommendation,
        IReadOnlyList<string> tags,
        string? fingerprint = null,
        double? metricValue = null,
        string? metricUnit = null,
        double? confidenceScore = null,
        IReadOnlyList<string>? caveats = null)
    {
        double score = confidenceScore ?? SeverityDefaultConfidence(severity);

        return new Claim
        {
            Fingerprint = !string.IsNullOrWhiteSpace(fingerprint) ? fingerprint : FindingFingerprint.Build(analyzer, category, title, tags),
            Severity = severity,
            Title = title,
            Narrative = narrative,
            Recommendation = recommendation,
            DerivedFrom = [],
            Confidence = new ConfidenceBreakdown
            {
                Composite = score,
                EvidenceStrength = score,
                IdentityFidelityCap = 1.0,
                TemporalAlignmentCap = 1.0,
                CapabilityFidelity = 1.0,
                ConflictPenalty = 1.0,
            },
            Caveats = caveats ?? [],
            Analyzer = analyzer,
            Category = category,
            Tags = tags,
            MetricValue = metricValue,
            MetricUnit = metricUnit,
        };
    }

    public static double SeverityDefaultConfidence(Severity severity) => severity switch
    {
        Severity.Critical => 0.9,
        Severity.Warning => 0.7,
        _ => 0.5,
    };
}
