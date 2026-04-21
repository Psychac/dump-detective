using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

internal sealed record ComposedReport(
    string DumpPath,
    DateTime GeneratedAtUtc,
    TimeSpan Elapsed,
    IReadOnlyList<ReportSection> Sections,
    DedupDiagnostics DedupDiagnostics,
    string ReportSchemaVersion = ReportContractVersions.ReportSchemaV1,
    string SectionSchemaVersion = ReportContractVersions.SectionSchemaV1,
    IReadOnlyList<DetailedAnalyzerSection>? DetailedAnalyzerSections = null,
    bool IsTrendReport = false,
    int TrendDumpCount = 0,
    IReadOnlyList<string>? TrendDumpPaths = null);

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

internal sealed record DetailedAnalyzerSection(
    string Title,
    string Content,
    IReadOnlyList<DetailedAnalyzerSubmodule>? Submodules = null);

internal enum DetailedAnalyzerSubmoduleKind
{
    Heading,
    Metric,
    Path,
    Text,
    ListItem,
    Divider,
    Empty
}

internal sealed record DetailedAnalyzerSubmodule(
    DetailedAnalyzerSubmoduleKind Kind,
    string? Label,
    string? Value,
    string? Text,
    int IndentLevel = 0);

internal sealed record DedupDiagnostics(
    int DuplicateCandidates,
    int MergedSections,
    int EvidenceBeforeMerge,
    int EvidenceAfterMerge,
    IReadOnlyList<string> MergedKeys);
