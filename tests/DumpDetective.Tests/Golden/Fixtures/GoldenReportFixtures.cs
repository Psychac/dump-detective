using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Tests.Golden.Fixtures;

internal static class GoldenReportFixtures
{
    public static ComposedReport Build(string fixtureName)
    {
        return fixtureName switch
        {
            "BaselineSmall" => BaselineSmall(),
            "DuplicateHeavy" => DuplicateHeavy(),
            "LongNames" => LongNames(),
            "RichEvidence" => RichEvidence(),
            "MixedSeverity" => MixedSeverity(),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "Unknown golden fixture")
        };
    }

    private static ComposedReport BaselineSmall() =>
        new(
            DumpPath: "C:/fixtures/BaselineSmall.dmp",
            GeneratedAtUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(12.3),
            Sections:
            [
                new ReportSection(
                    SectionKey: "baseline-small",
                    Title: "Leak pressure",
                    Category: "Leak",
                    Severity: FindingSeverity.Warning,
                    NarrativeSummary: "Detected duplicate strings.",
                    EvidenceRows:
                    [
                        new ReportEvidenceRow("Analyzer", "MemoryLeakAnalyzer"),
                        new ReportEvidenceRow("Value", "System.String duplicated")
                    ],
                    RemediationHints: ["Pool repeated string payloads."],
                    Fingerprints: ["baseline-small"])
            ],
            ExecutiveSummary: [],
            DeveloperActionPlan: [],
            DedupDiagnostics: new DedupDiagnostics(0, 0, 2, 2, []));

    private static ComposedReport DuplicateHeavy() =>
        new(
            DumpPath: "C:/fixtures/DuplicateHeavy.dmp",
            GeneratedAtUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(8.1),
            Sections:
            [
                new ReportSection(
                    SectionKey: "dup-heavy",
                    Title: "Duplicate-heavy merged section",
                    Category: "Leak",
                    Severity: FindingSeverity.Critical,
                    NarrativeSummary: "Merged duplicate leak evidence from multiple analyzers.",
                    EvidenceRows:
                    [
                        new ReportEvidenceRow("EvidenceA", "A repeated payload instance"),
                        new ReportEvidenceRow("EvidenceB", "Another repeated payload instance")
                    ],
                    RemediationHints: ["Deduplicate payload cache keys.", "Review object retention roots."],
                    Fingerprints: ["dup-heavy"])
            ],
            ExecutiveSummary: [],
            DeveloperActionPlan: [],
            DedupDiagnostics: new DedupDiagnostics(3, 3, 8, 2, ["dup-heavy"]));

    private static ComposedReport LongNames() =>
        new(
            DumpPath: "C:/fixtures/LongNames.dmp",
            GeneratedAtUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(4.2),
            Sections:
            [
                new ReportSection(
                    SectionKey: "long-names",
                    Title: "Long member/type names",
                    Category: "Memory",
                    Severity: FindingSeverity.Warning,
                    NarrativeSummary: "Long identifiers are preserved end-to-end.",
                    EvidenceRows:
                    [
                        new ReportEvidenceRow("Type", "VeryLongTypeName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOP"),
                        new ReportEvidenceRow("Member", "VeryLongMemberName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMN")
                    ],
                    RemediationHints: ["Keep full value visibility; do not truncate."],
                    Fingerprints: ["long-names"])
            ],
            ExecutiveSummary: [],
            DeveloperActionPlan: [],
            DedupDiagnostics: new DedupDiagnostics(0, 0, 2, 2, []));

    private static ComposedReport RichEvidence() =>
        new(
            DumpPath: "C:/fixtures/RichEvidence.dmp",
            GeneratedAtUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(9.8),
            Sections:
            [
                new ReportSection(
                    SectionKey: "rich-evidence",
                    Title: "Rich evidence sample",
                    Category: "Crash",
                    Severity: FindingSeverity.Warning,
                    NarrativeSummary: "Includes multiple evidence and remediation records.",
                    EvidenceRows:
                    [
                        new ReportEvidenceRow("Thread", "42"),
                        new ReportEvidenceRow("Exception", "System.NullReferenceException"),
                        new ReportEvidenceRow("StackTop", "Service.ProcessRequest")
                    ],
                    RemediationHints: ["Guard null dereferences.", "Add targeted telemetry around request processing."],
                    Fingerprints: ["rich-evidence"])
            ],
            ExecutiveSummary: [],
            DeveloperActionPlan: [],
            DedupDiagnostics: new DedupDiagnostics(0, 0, 3, 3, []));

    private static ComposedReport MixedSeverity() =>
        new(
            DumpPath: "C:/fixtures/MixedSeverity.dmp",
            GeneratedAtUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(6.6),
            Sections:
            [
                new ReportSection("sev-critical", "Critical leak", "Leak", FindingSeverity.Critical, "Critical item", [new ReportEvidenceRow("Item", "Critical")], ["Handle now"], ["sev-critical"]),
                new ReportSection("sev-warning", "Warning leak", "Leak", FindingSeverity.Warning, "Warning item", [new ReportEvidenceRow("Item", "Warning")], ["Plan remediation"], ["sev-warning"]),
                new ReportSection("sev-info", "Info signal", "Info", FindingSeverity.Info, "Informational item", [new ReportEvidenceRow("Item", "Info")], ["Observe"], ["sev-info"])
            ],
            ExecutiveSummary: [],
            DeveloperActionPlan: [],
            DedupDiagnostics: new DedupDiagnostics(0, 0, 3, 3, []));
}
