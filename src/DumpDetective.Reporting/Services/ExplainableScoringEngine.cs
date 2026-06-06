using DumpDetective.Core.Enums;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Computes explicit, weighted, explainable health scores for the three executive
/// dimensions: Leak, GC Pressure, and Thread Contention.
/// Replaces the previous category-string heuristic with a reproducible algorithm
/// whose score contributors are published in the report body.
/// </summary>
internal static class ExplainableScoringEngine
{
    // ── Points per severity tier ──────────────────────────────────────────────

    private const int CriticalBasePoints = 40;
    private const int WarningBasePoints = 20;
    private const int InfoBasePoints = 5;
    private const int ScoreCap = 100;

    // Heuristic confidence used when a finding carries no explicit ConfidenceScore.
    private const double HeuristicConfidence = 0.70;

    // ── Dimension category specs ──────────────────────────────────────────────

    private static readonly string[] LeakCategories = ["Leak", "Memory"];
    private static readonly string[] GcPressureCategories = ["Fragmentation", "GC", "LOH"];
    private static readonly string[] ThreadCategories = ["Hang", "Threading", "Deadlock", "Retention"];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes all three score breakdowns from the provided finding list.
    /// No heap allocation beyond the contributor lists; no LINQ.
    /// </summary>
    public static (ScoreBreakdown Leak, ScoreBreakdown GcPressure, ScoreBreakdown ThreadContention)
        ComputeScores(IReadOnlyList<FindingRecord> findings)
    {
        return (
            ComputeBreakdown("Leak", LeakCategories, findings),
            ComputeBreakdown("GcPressure", GcPressureCategories, findings),
            ComputeBreakdown("ThreadContention", ThreadCategories, findings)
        );
    }

    // ── Core breakdown computation ────────────────────────────────────────────

    private static ScoreBreakdown ComputeBreakdown(
        string dimension,
        string[] categories,
        IReadOnlyList<FindingRecord> findings)
    {
        var contributors = new List<ScoreContributor>();
        int rawScore = 0;
        double confidenceSum = 0.0;
        int confidenceCount = 0;

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord f = findings[i];
            if (!MatchesAnyCategory(f.Category, categories))
                continue;

            int basePoints = SeverityOrdinal(f.Severity) switch
            {
                2 => CriticalBasePoints,
                1 => WarningBasePoints,
                _ => InfoBasePoints,
            };

            // Confidence-weight the raw points.
            double weight = f.Confidence ?? 1.0;
            int points = (int)Math.Round(basePoints * weight);

            string? detail = f.Confidence.HasValue
                ? $"Confidence: {f.Confidence.Value:P0}"
                : null;

            contributors.Add(new ScoreContributor(
                Label: $"{f.Severity}: {f.Title}",
                Source: f.Analyzer,
                Points: points,
                Detail: detail));

            rawScore += points;

            if (f.Confidence.HasValue)
            {
                confidenceSum += f.Confidence.Value;
                confidenceCount++;
            }
        }

        int score = Math.Min(rawScore, ScoreCap);
        double confidence = contributors.Count == 0
            ? 0.0
            : confidenceCount > 0
                ? Math.Round(confidenceSum / confidenceCount, 2)
                : HeuristicConfidence;

        return new ScoreBreakdown(dimension, score, confidence, contributors);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool MatchesAnyCategory(string category, string[] categories)
    {
        for (int c = 0; c < categories.Length; c++)
        {
            if (category.Contains(categories[c], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0,
    };
}
