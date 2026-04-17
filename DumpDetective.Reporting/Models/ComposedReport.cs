using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

internal sealed record ComposedReport(
    string DumpPath,
    DateTime GeneratedAtUtc,
    TimeSpan Elapsed,
    IReadOnlyList<ReportSection> Sections,
    DedupDiagnostics DedupDiagnostics);

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

internal sealed record DedupDiagnostics(
    int DuplicateCandidates,
    int MergedSections,
    int EvidenceBeforeMerge,
    int EvidenceAfterMerge,
    IReadOnlyList<string> MergedKeys);
