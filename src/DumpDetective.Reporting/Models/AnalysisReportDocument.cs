using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Models;

internal sealed record AnalysisReportDocument
{
    public string SchemaVersion { get; init; } = "2.0";
    public string DumpPath { get; init; } = "";
    public DateTime GeneratedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public bool IsTrendReport { get; init; }
    public int TrendDumpCount { get; init; }
    public IReadOnlyList<string>? TrendDumpPaths { get; init; }

    // Cross-cutting outputs
    public IReadOnlyList<FindingRecord> Findings { get; init; } = [];
    public ExecutiveSummaryRecord? ExecutiveSummary { get; init; }        // null unless audience == Executive
    public IReadOnlyList<DeveloperActionRecord> DeveloperActionPlan { get; init; } = [];
    public IReadOnlyList<ConfidenceNote> Confidence { get; init; } = [];
    public DedupRecord DedupDiagnostics { get; init; } = new(0, 0, 0);

    // Per-analyzer structured sections — ordered by SortOrder
    public IReadOnlyList<AnalyzerDetailSection> AnalyzerSections { get; init; } = [];
    // Serialized raw artifacts produced by analyzers (CSV/JSON) when requested.
    public IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts { get; init; } = [];
}

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
}

internal sealed record ExecutiveSummaryRecord(
    long TotalManagedBytes,
    int LeakLikelihoodScore,        // 0–100
    int GcPressureScore,            // 0–100
    int ThreadContentionScore,      // 0–100
    IReadOnlyList<FindingRecord> TopRecommendations);   // Top 3 Critical/Warning findings

internal sealed record DeveloperActionRecord(
    string Priority,
    string Title,
    string Action,
    string Impact);

internal sealed record ConfidenceNote(
    string Analyzer,
    bool Capped,
    string Reason);

internal sealed record DedupRecord(
    int MergedSections,
    int DuplicateCandidates,
    int EvidenceBeforeMerge);
