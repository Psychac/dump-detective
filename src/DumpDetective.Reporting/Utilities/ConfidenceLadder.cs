namespace DumpDetective.Reporting.Utilities;

/// <summary>
/// The one confidence-score → band mapping for every report surface. <see cref="ConfidenceLevel.Key"/>
/// is serialized alongside each score so the HTML renderer reads the band instead of re-deriving it
/// from its own copy of the cut-offs — docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.4.
/// Kept in Reporting rather than on <c>Sdk.Synthesis.ConfidenceBreakdown</c>: band labels and symbols
/// are presentation, not part of the plugin-facing contract.
/// </summary>
internal static class ConfidenceLadder
{
    private static readonly ConfidenceLevel High = new("high", "High", "●●●●");
    private static readonly ConfidenceLevel MedHigh = new("medhigh", "Med-High", "●●●○");
    private static readonly ConfidenceLevel Medium = new("medium", "Medium", "●●○○");
    private static readonly ConfidenceLevel Low = new("low", "Low", "●○○○");

    public static ConfidenceLevel For(double score) =>
        score >= 0.85 ? High : score >= 0.65 ? MedHigh : score >= 0.45 ? Medium : Low;
}

internal readonly record struct ConfidenceLevel(string Key, string Label, string Symbol);
