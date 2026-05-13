using System.Text.Json.Serialization;

namespace DumpDetective.Reporting.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(SingleDumpReportDocument), typeDiscriminator: "single")]
[JsonDerivedType(typeof(TrendReportDocument), typeDiscriminator: "trend")]
internal abstract record AnalysisReportDocument
{
    public string SchemaVersion { get; init; } = "2.1";
    public DateTime GeneratedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public DumpDetective.Core.Models.AnalysisIncidentContext? IncidentContext { get; init; }

    // Cross-cutting outputs
    public IReadOnlyList<FindingRecord> Findings { get; init; } = [];
    public ExecutiveSummaryRecord? ExecutiveSummary { get; init; }        // null unless audience == Executive (or ReportAudience.All when enabled)
    public IReadOnlyList<DeveloperActionRecord> DeveloperActionPlan { get; init; } = [];
    public IReadOnlyList<ConfidenceNote> Confidence { get; init; } = [];

    // Per-analyzer structured sections — ordered by SortOrder
    public IReadOnlyList<AnalyzerDetailSection> AnalyzerSections { get; init; } = [];
    // Per-analyzer execution summary for report quality and diagnostics
    public IReadOnlyList<AnalyzerRunStatusRecord> AnalyzerRunStatuses { get; init; } = [];
    // Serialized raw artifacts produced by analyzers (CSV/JSON) when requested.
    public IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts { get; init; } = [];
}

internal sealed record SingleDumpReportDocument : AnalysisReportDocument
{
    public string DumpPath { get; init; } = "";
}

internal sealed record TrendReportDocument : AnalysisReportDocument
{
    public string DumpPath { get; init; } = "";
    public int TrendDumpCount { get; init; }
    public IReadOnlyList<string> TrendDumpPaths { get; init; } = [];
}

internal sealed record AnalyzerRunStatusRecord(
    string AnalyzerName,
    string Status,
    double DurationMs,
    int FindingCount,
    int WarningCount,
    long ObjectScanCount,
    string? ErrorMessage);

// Serializable projection of InsightFinding — InsightFinding itself is unchanged
internal sealed partial record FindingRecord(
    string Analyzer,
    string Category,
    string Severity,          // FindingSeverity.ToString()
    string Title,
    string Evidence,
    string Recommendation,
    IReadOnlyList<string> Tags,
    string Fingerprint);

// Backwards-compatible list properties for richer evidence/recommendation handling.
internal partial record FindingRecord
{
    public IReadOnlyList<string>? EvidenceItems { get; init; } = null;
    public IReadOnlyList<string>? RecommendationItems { get; init; } = null;
    public IReadOnlyList<string>? CaveatItems { get; init; } = null;
    public string? Cause { get; init; } = null;
    public string? Effect { get; init; } = null;
    public string? Fix { get; init; } = null;

    // P1.1: Traceability & actionability extensions
    public double? ConfidenceScore { get; init; } = null;                // 0.0–1.0
    public IReadOnlyList<EvidenceRef>? EvidenceRefs { get; init; } = null; // structured provenance
    public string? SuggestedOwner { get; init; } = null;                // owner/team suggestion
    public string? Effort { get; init; } = null;                       // e.g., "Low", "Medium", "High"
    public string? ValidationStep { get; init; } = null;               // short validation/check step
    public string? TrackingStatus { get; init; } = null;               // e.g., "Untracked", "InProgress", "Fixed"
}

internal sealed record EvidenceRef(
    string Analyzer,
    string? MetricKey = null,
    IReadOnlyList<string>? Addresses = null,
    string? ArtifactPath = null,
    int? SnapshotIndex = null);

internal sealed partial record ExecutiveSummaryRecord(
    long TotalManagedBytes,
    int LeakLikelihoodScore,        // 0–100
    int GcPressureScore,            // 0–100
    int ThreadContentionScore,      // 0–100
    IReadOnlyList<FindingRecord> TopRecommendations);   // Top 3 Critical/Warning findings

// P1.2: Explicit score breakdowns with contributors and trend-mode deltas
internal partial record ExecutiveSummaryRecord
{
    public IReadOnlyList<ScoreBreakdown>? ScoreBreakdowns { get; init; } = null;
    public int? LeakScoreDelta { get; init; } = null;
    public int? GcPressureScoreDelta { get; init; } = null;
    public int? ThreadContentionScoreDelta { get; init; } = null;
}

// P1.2: Scoring models
internal sealed record ScoreContributor(
    string Label,
    string Source,
    int Points,
    string? Detail = null);

internal sealed record ScoreBreakdown(
    string Dimension,
    int Score,
    double Confidence,
    IReadOnlyList<ScoreContributor> Contributors);

internal sealed record DeveloperActionRecord(
    string Priority,
    string Title,
    string Action,
    string Impact);

internal sealed record ConfidenceNote(
    string Analyzer,
    bool Capped,
    string Reason);

// DedupRecord removed — dedup diagnostics are no longer produced
