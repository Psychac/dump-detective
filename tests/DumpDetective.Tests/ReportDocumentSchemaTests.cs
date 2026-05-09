using System.Text.Json;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

/// <summary>
/// H2 — format-independent regression tests for <see cref="AnalysisReportDocument"/> JSON shape.
/// Round-trips through <see cref="ReportJsonContext"/> and asserts field values hold.
/// </summary>
public sealed class ReportDocumentSchemaTests
{
    private static readonly JsonSerializerOptions _indented =
        new(ReportJsonContext.Default.Options) { WriteIndented = true };

    [Fact]
    public void RoundTrip_PreservesAllTopLevelFields()
    {
        AnalysisReportDocument original = new()
        {
            DumpPath = "C:/dumps/test.dmp",
            GeneratedAtUtc = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            ElapsedSeconds = 42.5,
            IsTrendReport = false,
            DedupDiagnostics = new DedupRecord(MergedSections: 2, DuplicateCandidates: 3, EvidenceBeforeMerge: 5),
            Findings =
            [
                new FindingRecord(
                    Analyzer:       "RetentionAnalyzer",
                    Category:       "Leak",
                    Severity:       "Critical",
                    Title:          "Duplicate strings",
                    Evidence:       "1 000 000 duplicate System.String instances.",
                    Recommendation: "Pool repeated string payloads.",
                    Tags:           ["memory", "string"],
                    Fingerprint:    "dup-strings-01")
            ],
            AnalyzerSections =
            [
                new AnalyzerDetailSection("RetentionAnalyzer", "Retention Analysis", 25,
                [
                    new HeadingBlock("OVERALL SUMMARY"),
                    new MetricBlock("Total Strings", "1,000,000", 1_000_000),
                    new DividerBlock(),
                    new TableBlock(
                        Caption: "Top duplicate strings",
                        Headers: ["Value", "Count", "Wasted"],
                        Rows:
                        [
                            new TableRow([new TableCell("hello", 500), new TableCell("500"), new TableCell("8 KB")])
                        ])
                ])
            ]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        restored!.SchemaVersion.Should().Be("2.1");
        restored.DumpPath.Should().Be("C:/dumps/test.dmp");
        restored.ElapsedSeconds.Should().BeApproximately(42.5, 0.001);
        restored.IsTrendReport.Should().BeFalse();
        restored.DedupDiagnostics.MergedSections.Should().Be(2);
        restored.DedupDiagnostics.DuplicateCandidates.Should().Be(3);

        restored.Findings.Should().HaveCount(1);
        FindingRecord f = restored.Findings[0];
        f.Analyzer.Should().Be("RetentionAnalyzer");
        f.Severity.Should().Be("Critical");
        f.Title.Should().Be("Duplicate strings");
        f.Fingerprint.Should().Be("dup-strings-01");
        f.Tags.Should().Contain("memory").And.Contain("string");
    }

    [Fact]
    public void RoundTrip_PreservesSectionBlockPolymorphism()
    {
        AnalysisReportDocument original = new()
        {
            DumpPath = "C:/test.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            AnalyzerSections =
            [
                new AnalyzerDetailSection("X", "X", 0,
                [
                    new HeadingBlock("H"),
                    new MetricBlock("Label", "Val", 1.5, 1),
                    new PathBlock("P", "C:/some/path"),
                    new TextBlock("text here"),
                    new ListItemBlock("item"),
                    new DividerBlock(),
                    new BlankBlock(),
                    new TableBlock("cap", ["A","B"], [new TableRow([new TableCell("a", 1L), new TableCell("b")])]),
                    new CollapsibleSectionBeginBlock("group"),
                    new CollapsibleSectionEndBlock()
                ])
            ]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        IReadOnlyList<SectionBlock> blocks = restored!.AnalyzerSections[0].Blocks;
        blocks.Should().HaveCount(10);
        blocks[0].Should().BeOfType<HeadingBlock>().Which.Text.Should().Be("H");
        blocks[1].Should().BeOfType<MetricBlock>().Which.Label.Should().Be("Label");
        blocks[2].Should().BeOfType<PathBlock>().Which.Path.Should().Be("C:/some/path");
        blocks[3].Should().BeOfType<TextBlock>().Which.Text.Should().Be("text here");
        blocks[4].Should().BeOfType<ListItemBlock>().Which.Text.Should().Be("item");
        blocks[5].Should().BeOfType<DividerBlock>();
        blocks[6].Should().BeOfType<BlankBlock>();
        blocks[7].Should().BeOfType<TableBlock>().Which.Caption.Should().Be("cap");
        blocks[8].Should().BeOfType<CollapsibleSectionBeginBlock>().Which.Title.Should().Be("group");
        blocks[9].Should().BeOfType<CollapsibleSectionEndBlock>();

        // Verify TableCell RawValue survives round-trip
        TableBlock table = (TableBlock)blocks[7];
        table.Rows[0].Cells[0].RawValue.Should().Be(1L);
        table.Rows[0].Cells[1].RawValue.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_PreservesTrendFields()
    {
        AnalysisReportDocument original = new()
        {
            DumpPath = "C:/trend.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            IsTrendReport = true,
            TrendDumpCount = 3,
            TrendDumpPaths = ["C:/d1.dmp", "C:/d2.dmp", "C:/d3.dmp"]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored!.IsTrendReport.Should().BeTrue();
        restored.TrendDumpCount.Should().Be(3);
        restored.TrendDumpPaths.Should().HaveCount(3).And.Contain("C:/d2.dmp");
    }

    [Fact]
    public void RoundTrip_ExecutiveSummaryIsNullWhenAbsent()
    {
        AnalysisReportDocument original = new() { DumpPath = "C:/t.dmp", GeneratedAtUtc = DateTime.UtcNow };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);

        json.Should().NotContain("\"executiveSummary\""); // WhenWritingNull suppresses it
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);
        restored!.ExecutiveSummary.Should().BeNull();
    }

    [Fact]
    public void JsonShape_UsesLowerCamelCase()
    {
        AnalysisReportDocument original = new()
        {
            DumpPath = "C:/t.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1.0,
            Findings = [new FindingRecord("A", "B", "Info", "T", "E", "R", [], "fp")]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);

        json.Should().Contain("\"dumpPath\"");
        json.Should().Contain("\"elapsedSeconds\"");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"findings\"");
        json.Should().Contain("\"fingerprint\"");
        json.Should().NotContain("\"DumpPath\"");
        json.Should().NotContain("\"ElapsedSeconds\"");
    }
}
