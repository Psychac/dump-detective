using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Core.Models;

/// <summary>
/// The confidence a report shows for a claim. A claim whose breakdown limits nothing (composite 1.0) carries no
/// measured information about its confidence, so the report shows the severity-based default it has always shown;
/// only a measured limitation overrides it. This is the rule the report has always applied to claims, kept so the claim
/// unification changed no shown number.
/// </summary>
public static class ClaimReportExtensions
{
    public static double ReportConfidence(this Claim claim) =>
        claim.Confidence.Composite < 1.0
            ? claim.Confidence.Composite
            : ClaimFactory.SeverityDefaultConfidence(claim.Severity);

    /// <summary>What measurably lowered <see cref="ReportConfidence"/>; empty when the shown score is the severity default.</summary>
    public static IReadOnlyList<string> ReportConfidenceLimitingFactors(this Claim claim) =>
        claim.Confidence.Composite < 1.0 ? claim.Confidence.LimitingFactors : [];
}
