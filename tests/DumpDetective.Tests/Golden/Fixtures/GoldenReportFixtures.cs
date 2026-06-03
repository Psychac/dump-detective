using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Tests.Golden.Fixtures;

internal static class GoldenReportFixtures
{
    public static SingleDumpReportDocument Build(string fixtureName)
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

    private static SingleDumpReportDocument BaselineSmall() => new()
    {
        DumpPath = "C:/fixtures/BaselineSmall.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 12.3,
        Findings =
        [
            new FindingRecord(
                Id:             "baseline-small",
                Analyzer:       "RetentionAnalyzer",
                Category:       "Leak",
                Severity:       "Warning",
                Title:          "Leak pressure",
                Details:        ["Detected duplicate strings.", "- Analyzer: RetentionAnalyzer", "- Value: System.String duplicated"],
                Recommendation: "Pool repeated string payloads.",
                Tags:           ["baseline-small"])
        ],
        
    };

    private static SingleDumpReportDocument DuplicateHeavy() => new()
    {
        DumpPath = "C:/fixtures/DuplicateHeavy.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 8.1,
        Findings =
        [
            new FindingRecord(
                Id:             "dup-heavy",
                Analyzer:       "RetentionAnalyzer",
                Category:       "Leak",
                Severity:       "Critical",
                Title:          "Duplicate-heavy merged section",
                Details:        ["Merged duplicate leak evidence from multiple analyzers.", "- EvidenceA: A repeated payload instance", "- EvidenceB: Another repeated payload instance"],
                Recommendation: "Deduplicate payload cache keys. Review object retention roots.",
                Tags:           ["dup-heavy"])
        ],
        
    };

    private static SingleDumpReportDocument LongNames() => new()
    {
        DumpPath = "C:/fixtures/LongNames.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 4.2,
        Findings =
        [
            new FindingRecord(
                Id:             "long-names",
                Analyzer:       "MemoryAnalyzer",
                Category:       "Memory",
                Severity:       "Warning",
                Title:          "Long member/type names",
                Details:        ["Long identifiers are preserved end-to-end.", "- Type: VeryLongTypeName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOP", "- Member: VeryLongMemberName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMN"],
                Recommendation: "Keep full value visibility; do not truncate.",
                Tags:           ["long-names"])
        ],
        
    };

    private static SingleDumpReportDocument RichEvidence() => new()
    {
        DumpPath = "C:/fixtures/RichEvidence.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 9.8,
        Findings =
        [
            new FindingRecord(
                Id:             "rich-evidence",
                Analyzer:       "CrashAnalyzer",
                Category:       "Crash",
                Severity:       "Warning",
                Title:          "Rich evidence sample",
                Details:        ["Includes multiple evidence and remediation records.", "- Thread: 42", "- Exception: System.NullReferenceException", "- StackTop: Service.ProcessRequest"],
                Recommendation: "Guard null dereferences. Add targeted telemetry around request processing.",
                Tags:           ["rich-evidence"])
        ],
        
    };

    private static SingleDumpReportDocument MixedSeverity() => new()
    {
        DumpPath = "C:/fixtures/MixedSeverity.dmp",
        GeneratedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ElapsedSeconds = 6.6,
        Findings =
        [
            new FindingRecord("sev-critical", "LeakAnalyzer", "Leak", "Critical", "Critical leak", ["Critical item"], "Handle now", ["sev-critical"]),
            new FindingRecord("sev-warning", "LeakAnalyzer", "Leak", "Warning",  "Warning leak",   ["Warning item"], "Plan remediation",  ["sev-warning"]),
            new FindingRecord("sev-info", "LeakAnalyzer", "Info", "Info",     "Info signal",    ["Informational item"], "Observe",           ["sev-info"])
        ],
        
    };
}

