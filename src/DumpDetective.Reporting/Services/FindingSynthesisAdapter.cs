using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Translates a synthesis-rule <see cref="Finding"/> into the report pipeline's
/// <see cref="InsightFinding"/> — Option 1 from
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook: the report
/// pipeline (<c>ActionPriorityService</c>, <c>CorrelationBuilder</c>, every section builder and
/// formatter) keeps reading <see cref="InsightFinding"/> unchanged; only the point where
/// <c>IFindingGenerator</c> used to sit is replaced. Throwaway once the report pipeline itself
/// reads <see cref="Finding"/> natively — not a permanent architecture.
/// </summary>
internal static class FindingSynthesisAdapter
{
    public static InsightFinding ToInsightFinding(Finding finding) => new(
        Analyzer: finding.Analyzer,
        Category: finding.Category,
        Severity: ToFindingSeverity(finding.Severity),
        Title: finding.Title,
        Evidence: finding.Narrative,
        Recommendation: finding.Recommendation,
        Tags: finding.Tags,
        Fingerprint: finding.Fingerprint,
        MetricValue: finding.MetricValue,
        MetricUnit: finding.MetricUnit,
        ConfidenceScore: finding.Confidence.Composite,
        Caveats: finding.Caveats.Count > 0 ? finding.Caveats : null);

    private static FindingSeverity ToFindingSeverity(Severity severity) => severity switch
    {
        Severity.Info => FindingSeverity.Info,
        Severity.Warning => FindingSeverity.Warning,
        Severity.Critical => FindingSeverity.Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unmapped Sdk.Synthesis.Severity value."),
    };
}
