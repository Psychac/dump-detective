using System.Text.Json.Serialization;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

// ── Health Scorecard ──────────────────────────────────────────────────────────

internal enum DomainSeverity { Unknown, OK, Warning, Critical }

internal enum DomainSeverityChange { Stable, Improved, Regressed, NewDomain, Removed }

internal sealed record DomainHealthEntry(
    string Domain,
    DomainSeverity Severity,
    int FindingCount,
    int CriticalCount,
    int WarningCount,
    // Trend-mode additions (null in single-dump mode):
    DomainSeverity? BaselineSeverity = null,
    DomainSeverityChange? Change = null,
    // Per-snapshot severity across all dumps (index 0 = baseline, last = current).
    // Null in single-dump mode or when only 2 snapshots (baseline/current already cover it).
    IReadOnlyList<DomainSeverity>? SeverityHistory = null);

internal sealed record HealthScorecard(
    IReadOnlyList<DomainHealthEntry> Domains,
    DomainSeverity OverallSeverity);

// ─────────────────────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(SingleDumpReportDocument), typeDiscriminator: "single")]
[JsonDerivedType(typeof(TrendReportDocument), typeDiscriminator: "trend")]
internal abstract record AnalysisReportDocument
{
    public string SchemaVersion { get; init; } = "2.1";
    // Version for deterministic scoring/ranking semantics used by this report.
    public string? ScoringModelVersion { get; init; } = null;
    // Rendering contract: "client" for full client render, "prerendered" when server pre-rendered heavy sections.
    public string RenderMode { get; init; } = "client";
    // Presentation contract: "v1" (default) or "v2" for visual-system renderer behavior.
    public string ReportStyleVersion { get; init; } = "v1";
    public DateTime GeneratedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? AnalyzerVersion { get; init; }
    public DumpDetective.Core.Models.AnalysisIncidentContext? IncidentContext { get; init; }
    public HealthScorecard? HealthScorecard { get; init; }
    public IReadOnlyList<ReportDomainSection>? Domains { get; init; } = null;
    public IReadOnlyList<FindingRecord>? CrossDomainInsights { get; init; } = null;
    public IReadOnlyList<CorrelationEventRecord>? CorrelationEvents { get; init; } = null;
    public ReportAppendix? Appendix { get; init; } = null;
    public ExecutiveSummaryRecord? ExecutiveSummary { get; init; }        // null unless audience == Executive (or ReportAudience.All when enabled)

    [JsonIgnore]
    public IReadOnlyList<FindingRecord> Findings { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<AnalyzerDetailSection> AnalyzerSections { get; init; } = [];
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
    public int TrendNewFindingCount { get; init; }
    public int TrendPersistentFindingCount { get; init; }
    public int TrendResolvedFindingCount { get; init; }
    /// <summary>
    /// Serialized trend sections (T2–T7) for the JavaScript renderer.
    /// These mirror <see cref="AnalysisReportDocument.AnalyzerSections"/> but are NOT [JsonIgnore],
    /// so they are available in the embedded JSON that the JS reads.
    /// </summary>
    public IReadOnlyList<AnalyzerDetailSection> TrendAnalyzerSections { get; init; } = [];

    /// <summary>
    /// Full per-dump documents — [JsonIgnore] because they are serialized separately
    /// in the HTML renderer using the proven AnalysisReportDocument serializer path.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AnalysisReportDocument> PerDumpDocuments { get; init; } = [];
}

internal sealed record AnalyzerRunStatusRecord(
    string AnalyzerName,
    string Status,
    double DurationMs,
    int FindingCount,
    int WarningCount,
    long ObjectScanCount,
    long CacheHits,
    long CacheMisses,
    string? ErrorMessage,
    string? FindingGeneratorError = null,
    string? SkipReason = null);

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
    // T9: Trend regression baseline/current values
    public double? MetricBaseline { get; init; } = null;
    public double? MetricCurrent  { get; init; } = null;
    public string? MetricUnit     { get; init; } = null;
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
    public HealthScorecard? HealthScorecard { get; init; } = null;
    public IReadOnlyList<FindingRecord>? CriticalFindings { get; init; } = null;
    public IReadOnlyList<FindingRecord>? WarningFindings { get; init; } = null;
    public IReadOnlyList<ScoreBreakdown>? ScoreBreakdowns { get; init; } = null;
    public int? LeakScoreDelta { get; init; } = null;
    public int? GcPressureScoreDelta { get; init; } = null;
    public int? ThreadContentionScoreDelta { get; init; } = null;
    // Trend-mode highlight tables (T2c/T2d)
    public IReadOnlyList<FindingRecord>? TopRegressions { get; init; } = null;
    public IReadOnlyList<FindingRecord>? TopImprovements { get; init; } = null;

    // Key metrics strip — sourced from domain results
    public long? LohBytes { get; init; } = null;
    public double? LohPercent { get; init; } = null;
    public double? Gen2Percent { get; init; } = null;
    public int? LeakCandidateCount { get; init; } = null;
    public int? HangScore { get; init; } = null;
    public int? BlockedThreads { get; init; } = null;
    public int? DeadlockCycles { get; init; } = null;
    public int? ActiveExceptions { get; init; } = null;
    public int? FinalizerQueueCount { get; init; } = null;
    public int? TotalObjects { get; init; } = null;
    public int? UniqueTypes { get; init; } = null;
    public string? GcPressureLevel { get; init; } = null;

    // B1: deterministic, explainable top actions for action-queue rendering.
    public IReadOnlyList<RankedActionRecord>? TopActions { get; init; } = null;
    public string? ActionScoringModelVersion { get; init; } = null;
}

internal sealed record ActionPriorityFactors(
    int SeverityWeight,
    int BlastRadiusWeight,
    int ImpactLikelihoodWeight,
    int TimeToMitigateWeight,
    int ConfidenceWeight,
    int DependencyRiskWeight,
    int TotalScore);

internal sealed record RankedActionRecord(
    int Priority,
    string Title,
    string Action,
    string Impact,
    string WhyNow,
    string FindingFingerprint,
    string Analyzer,
    string? Owner = null,
    string? Effort = null,
    string? Status = null,
    string? Validation = null,
    ActionConfidenceRecord? Confidence = null,
    ActionPriorityFactors? Factors = null);

internal sealed record ActionConfidenceRecord(
    double EvidenceCompleteness,
    double CrossAnalyzerConsistency,
    double HeuristicPenalty,
    double CoverageFreshness,
    double Composite,
    IReadOnlyList<string>? Caveats = null);

internal sealed record CorrelationEventRecord(
    string EventType,
    string Title,
    string Rationale,
    string Confidence,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> SignalKeys,
    IReadOnlyList<string> SourceFingerprints);

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

internal sealed record ReportAppendix(
    IReadOnlyList<AnalyzerRunStatusRecord> AnalyzerRunSummary,
    IReadOnlyList<AnalyzerMemoryDiagnosticRecord>? MemoryDiagnostics,
    IReadOnlyList<string> KnownLimitations);

internal sealed record AnalyzerMemoryDiagnosticRecord(
    string AnalyzerName,
    long WorkingSetBefore,
    long WorkingSetAfter,
    long WorkingSetDelta,
    long ManagedHeapBefore,
    long ManagedHeapAfter,
    long ManagedHeapDelta);

internal sealed record ReportDomainSection(
    string Domain,
    FindingSeverity? LeadSeverity,
    IReadOnlyList<AnalyzerDetailSection> Sections,
    IReadOnlyList<FindingRecord> DomainInsights);

// DedupRecord removed — dedup diagnostics are no longer produced
