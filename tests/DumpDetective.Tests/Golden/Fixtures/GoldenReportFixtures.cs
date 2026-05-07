using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Tests.Golden.Fixtures;

internal static class GoldenReportFixtures
{
    public static AnalysisReportDocument Build(string fixtureName)
    {
        return fixtureName switch
        {
            "BaselineSmall"  => BaselineSmall(),
            "DuplicateHeavy" => DuplicateHeavy(),
            "LongNames"      => LongNames(),
            "RichEvidence"   => RichEvidence(),
            "MixedSeverity"  => MixedSeverity(),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "Unknown golden fixture")
        };
    }

    private static AnalysisReportDocument BaselineSmall() => new()
    {
        DumpPath       = "C:/fixtures/BaselineSmall.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 12.3,
        Findings =
        [
            new FindingRecord(
                Analyzer:       "RetentionAnalyzer",
                Category:       "Leak",
                Severity:       "Warning",
                Title:          "Leak pressure",
                Evidence:       "Detected duplicate strings.\n- Analyzer: RetentionAnalyzer\n- Value: System.String duplicated",
                Recommendation: "Pool repeated string payloads.",
                Tags:           ["baseline-small"],
                Fingerprint:    "baseline-small")
        ],
        DedupDiagnostics = new DedupRecord(MergedSections: 0, DuplicateCandidates: 0, EvidenceBeforeMerge: 2)
    };

    private static AnalysisReportDocument DuplicateHeavy() => new()
    {
        DumpPath       = "C:/fixtures/DuplicateHeavy.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 8.1,
        Findings =
        [
            new FindingRecord(
                Analyzer:       "RetentionAnalyzer",
                Category:       "Leak",
                Severity:       "Critical",
                Title:          "Duplicate-heavy merged section",
                Evidence:       "Merged duplicate leak evidence from multiple analyzers.\n- EvidenceA: A repeated payload instance\n- EvidenceB: Another repeated payload instance",
                Recommendation: "Deduplicate payload cache keys. Review object retention roots.",
                Tags:           ["dup-heavy"],
                Fingerprint:    "dup-heavy")
        ],
        DedupDiagnostics = new DedupRecord(MergedSections: 3, DuplicateCandidates: 3, EvidenceBeforeMerge: 8)
    };

    private static AnalysisReportDocument LongNames() => new()
    {
        DumpPath       = "C:/fixtures/LongNames.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 4.2,
        Findings =
        [
            new FindingRecord(
                Analyzer:       "MemoryAnalyzer",
                Category:       "Memory",
                Severity:       "Warning",
                Title:          "Long member/type names",
                Evidence:       "Long identifiers are preserved end-to-end.\n- Type: VeryLongTypeName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOP\n- Member: VeryLongMemberName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMN",
                Recommendation: "Keep full value visibility; do not truncate.",
                Tags:           ["long-names"],
                Fingerprint:    "long-names")
        ],
        DedupDiagnostics = new DedupRecord(MergedSections: 0, DuplicateCandidates: 0, EvidenceBeforeMerge: 2)
    };

    private static AnalysisReportDocument RichEvidence() => new()
    {
        DumpPath       = "C:/fixtures/RichEvidence.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 9.8,
        Findings =
        [
            new FindingRecord(
                Analyzer:       "CrashAnalyzer",
                Category:       "Crash",
                Severity:       "Warning",
                Title:          "Rich evidence sample",
                Evidence:       "Includes multiple evidence and remediation records.\n- Thread: 42\n- Exception: System.NullReferenceException\n- StackTop: Service.ProcessRequest",
                Recommendation: "Guard null dereferences. Add targeted telemetry around request processing.",
                Tags:           ["rich-evidence"],
                Fingerprint:    "rich-evidence")
        ],
        DedupDiagnostics = new DedupRecord(MergedSections: 0, DuplicateCandidates: 0, EvidenceBeforeMerge: 3)
    };

    private static AnalysisReportDocument MixedSeverity() => new()
    {
        DumpPath       = "C:/fixtures/MixedSeverity.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 6.6,
        Findings =
        [
            new FindingRecord("LeakAnalyzer", "Leak", "Critical", "Critical leak",  "Critical item",     "Handle now",        ["sev-critical"], "sev-critical"),
            new FindingRecord("LeakAnalyzer", "Leak", "Warning",  "Warning leak",   "Warning item",      "Plan remediation",  ["sev-warning"],  "sev-warning"),
            new FindingRecord("LeakAnalyzer", "Info", "Info",     "Info signal",    "Informational item","Observe",           ["sev-info"],     "sev-info")
        ],
        DedupDiagnostics = new DedupRecord(MergedSections: 0, DuplicateCandidates: 0, EvidenceBeforeMerge: 3)
    };
}

