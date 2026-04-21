using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

internal sealed record ComposedReport(
    string DumpPath,
    DateTime GeneratedAtUtc,
    TimeSpan Elapsed,
    IReadOnlyList<ReportSection> Sections,
    DedupDiagnostics DedupDiagnostics,
    string DetailedAnalyzerReport,
    string ReportSchemaVersion = ReportContractVersions.ReportSchemaV1,
    string SectionSchemaVersion = ReportContractVersions.SectionSchemaV1,
    IReadOnlyList<DetailedAnalyzerSection>? DetailedAnalyzerSections = null);

internal static class ReportContractVersions
{
    public const string ReportSchemaV1 = "1.0";
    public const string SectionSchemaV1 = "1.0";
}

internal sealed record ReportSection(
    string SectionKey,
    string Title,
    string Category,
    FindingSeverity Severity,
    string NarrativeSummary,
    IReadOnlyList<ReportEvidenceRow> EvidenceRows,
    IReadOnlyList<string> RemediationHints,
    IReadOnlyList<string> Fingerprints);

internal sealed record ReportEvidenceRow(string Label, string Value);

internal sealed record DetailedAnalyzerSection(string Title, string Content);

internal sealed record DedupDiagnostics(
    int DuplicateCandidates,
    int MergedSections,
    int EvidenceBeforeMerge,
    int EvidenceAfterMerge,
    IReadOnlyList<string> MergedKeys);
