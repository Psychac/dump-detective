using System.Text.Json.Serialization;
using System.Collections.Generic;

using DumpDetective.Core.Enums;

namespace DumpDetective.Reporting.Models;

// ── Health Scorecard ──────────────────────────────────────────────────────────

internal enum DomainSeverity { Unknown, OK, Warning, Critical }

internal enum DomainSeverityChange { Stable, Improved, Regressed, NewDomain, Removed }

internal enum DomainConfidenceTrend { Low, Medium, High }

internal sealed record DomainHealthEntry(
    [property: JsonIgnore]
    string Domain,
    [property: JsonPropertyName("sev")]
    DomainSeverity Severity,
    [property: JsonPropertyName("find")]
    int FindingCount,
    [property: JsonPropertyName("crit")]
    int CriticalCount,
    [property: JsonPropertyName("warn")]
    int WarningCount,
    // Trend-mode additions (null in single-dump mode):
    [property: JsonPropertyName("baseCrit")]
    int? BaselineCriticalCount = null,
    [property: JsonPropertyName("deltaCrit")]
    int? DeltaCritical = null,
    [property: JsonPropertyName("baseWarn")]
    int? BaselineWarningCount = null,
    [property: JsonPropertyName("deltaWarn")]
    int? DeltaWarning = null,
    [property: JsonPropertyName("peakCrit")]
    int? PeakCriticalCount = null,
    [property: JsonPropertyName("peakCritIdx")]
    int? PeakCriticalSnapshotIndex = null,
    [property: JsonPropertyName("peakWarn")]
    int? PeakWarningCount = null,
    [property: JsonPropertyName("peakWarnIdx")]
    int? PeakWarningSnapshotIndex = null,
    [property: JsonPropertyName("baseSev")]
    DomainSeverity? BaselineSeverity = null,
    [property: JsonPropertyName("change")]
    DomainSeverityChange? Change = null,
    // Per-snapshot severity across all dumps (index 0 = baseline, last = current).
    // Null in single-dump mode or when only 2 snapshots (baseline/current already cover it).
    [property: JsonPropertyName("hist")]
    IReadOnlyList<DomainSeverity>? SeverityHistory = null,
    [property: JsonPropertyName("vel")]
    double? VelocityScore = null,
    [property: JsonPropertyName("vol")]
    double? VolatilityScore = null,
    [property: JsonPropertyName("conf")]
    string? ConfidenceTrend = null);

internal sealed record TrendSummary(
    int DomainsRegressed,
    int DomainsImproved,
    int DomainsNew,
    int DomainsRemoved,
    int NewCriticals,
    int ResolvedCriticals,
    int NetCriticalChange,
    int NewWarnings,
    int ResolvedWarnings,
    int NetWarningChange);

internal sealed record HealthScorecard(
    IReadOnlyDictionary<string, DomainHealthEntry> Domains,
    DomainSeverity OverallSeverity,
    TrendSummary? Trend = null);

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
    public Core.Models.AnalysisIncidentContext? IncidentContext { get; init; }
    public HealthScorecard? HealthScorecard { get; init; }
    public IReadOnlyList<ReportDomainSection>? Domains { get; init; } = null;
    public IReadOnlyList<FindingRecord>? CrossDomainInsights { get; init; } = null;
    public IReadOnlyList<CorrelationEventRecord>? CorrelationEvents { get; init; } = null;
    public ReportAppendix? Appendix { get; init; } = null;
    public ExecutiveSummaryRecord? ExecutiveSummary { get; init; }        // optional executive summary

    [JsonIgnore]
    public IReadOnlyList<FindingRecord> Findings { get; init; } = Array.Empty<FindingRecord>();

    [JsonIgnore]
    public IReadOnlyList<AnalyzerDetailSection> AnalyzerSections { get; init; } = Array.Empty<AnalyzerDetailSection>();
}

internal sealed record SingleDumpReportDocument : AnalysisReportDocument
{
    public string DumpPath { get; init; } = "";
}

internal sealed record TrendReportDocument : AnalysisReportDocument
{
    public int TrendDumpCount { get; init; }
    public IReadOnlyList<string> TrendDumpPaths { get; init; } = Array.Empty<string>();
    public int TrendNewFindingCount { get; init; }
    public int TrendPersistentFindingCount { get; init; }
    public int TrendResolvedFindingCount { get; init; }
    public TrendStoryRecord? TrendStory { get; init; } = null;
    /// <summary>
    /// Serialized trend sections (T2–T7) for the JavaScript renderer.
    /// These mirror <see cref="AnalysisReportDocument.AnalyzerSections"/> but are NOT [JsonIgnore],
    /// so they are available in the embedded JSON that the JS reads.
    /// </summary>
    public IReadOnlyList<AnalyzerDetailSection> TrendAnalyzerSections { get; init; } = Array.Empty<AnalyzerDetailSection>();

    /// <summary>
    /// Full per-dump documents — [JsonIgnore] because they are serialized separately
    /// in the HTML renderer using the proven AnalysisReportDocument serializer path.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AnalysisReportDocument> PerDumpDocuments { get; init; } = Array.Empty<AnalysisReportDocument>();
}

internal sealed record TrendStoryRecord(
    string Summary,
    string FirstMajorRegression,
    string LargestInflection,
    string[] TopWorseningDomains,
    string[] LikelyCouplingHints,
    string[] MetricReferences);

internal enum RegressionClass
{
    NewRisk,
    AmplifiedRisk,
    VolatileRisk
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

// Compact serializable projection of InsightFinding used in generated JSON
internal sealed record FindingRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("analyzer")] string Analyzer,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("details")]
    IReadOnlyList<string>? Details,
    [property: JsonPropertyName("recommendation")] string Recommendation,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags)
{
    // Traceability & actionability extensions (optional)
    [property: JsonPropertyName("confidence")]
    public double? Confidence { get; init; } = null;

    [property: JsonPropertyName("refs")]
    public IReadOnlyList<EvidenceRef>? Refs { get; init; } = null;

    // Regression/metric context
    [property: JsonPropertyName("metricBaseline")]
    public double? MetricBaseline { get; init; } = null;

    [property: JsonPropertyName("metricCurrent")]
    public double? MetricCurrent { get; init; } = null;

    [property: JsonPropertyName("metricUnit")]
    public string? MetricUnit { get; init; } = null;

    [property: JsonPropertyName("regressionClass")]
    public string? RegressionClass { get; init; } = null;
    
    [property: JsonPropertyName("caveats")]
    public IReadOnlyList<string>? Caveats { get; init; } = null;
}

// (compact contract implemented directly on FindingRecord above)

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
    int ThreadContentionScore       // 0–100
    );

// P1.2: Explicit score breakdowns with contributors and trend-mode deltas
internal partial record ExecutiveSummaryRecord
{
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
    string EventId,
    string EventType,
    string Title,
    string Rationale,
    double Confidence,
    IReadOnlyList<string> Domains,
    IReadOnlyList<int> SnapshotIndices,
    IReadOnlyList<string> SignalKeys,
    IReadOnlyList<string> SourceFingerprints,
    int? PrimarySnapshotIndex = null);

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
    [property: JsonPropertyName("an")] string AnalyzerName,
    [property: JsonPropertyName("wsB")] long WorkingSetBefore,
    [property: JsonPropertyName("wsA")] long WorkingSetAfter,
    [property: JsonPropertyName("mhB")] long ManagedHeapBefore,
    [property: JsonPropertyName("mhA")] long ManagedHeapAfter);

internal sealed record ReportDomainSection(
    string Domain,
    FindingSeverity? LeadSeverity,
    IReadOnlyList<AnalyzerDetailSection> Sections,
    IReadOnlyList<FindingRecord> DomainInsights);

// DedupRecord removed — dedup diagnostics are no longer produced
