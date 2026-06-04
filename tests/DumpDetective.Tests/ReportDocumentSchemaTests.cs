using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

/// <summary>
/// H2 — format-independent regression tests for the polymorphic report JSON shape.
/// Round-trips through <see cref="ReportJsonContext"/> and asserts field values hold.
/// </summary>
public sealed class ReportDocumentSchemaTests
{
    private static readonly JsonSerializerOptions _indented =
        new(ReportJsonContext.Default.Options) { WriteIndented = true };

    [Fact]
    public void RoundTrip_PreservesModernTopLevelFields()
    {
        SingleDumpReportDocument original = new()
        {
            DumpPath = "C:/dumps/test.dmp",
            ScoringModelVersion = "v1",
            GeneratedAtUtc = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            ElapsedSeconds = 42.5,
            HealthScorecard = new HealthScorecard(
                Domains: new System.Collections.Generic.Dictionary<string, DomainHealthEntry> { ["Memory"] = new DomainHealthEntry("Memory", DomainSeverity.Critical, 1, 1, 0) },
                OverallSeverity: DomainSeverity.Critical),
            Domains =
            [
                new ReportDomainSection(
                    Domain: "Memory",
                    LeadSeverity: DumpDetective.Core.Models.FindingSeverity.Critical,
                    Sections:
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
                    ],
                            DomainInsights: [new FindingRecord("dup-strings-01", "RetentionAnalyzer", "Leak", "Critical", "Duplicate strings", ["1 000 000 duplicate System.String instances."], "Pool repeated string payloads.", ["memory", "string"])] )
            ],
                    CrossDomainInsights = [new FindingRecord("cross-01", "InsightEngine", "CrossDomain", "Warning", "Cross-domain issue", ["Evidence"], "Recommendation", [])],
            Appendix = new ReportAppendix(
                AnalyzerRunSummary: [new AnalyzerRunStatusRecord("RetentionAnalyzer", "Success", 42.5, 1, 0, 123, 4, 5, null)],
                MemoryDiagnostics: [new AnalyzerMemoryDiagnosticRecord("RetentionAnalyzer", 10, 12, 2, 3, 4, 1)],
                KnownLimitations: ["Limited sample"])
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        restored!.SchemaVersion.Should().Be("2.1");
        restored.ScoringModelVersion.Should().Be("v1");
        restored.ElapsedSeconds.Should().BeApproximately(42.5, 0.001);
        restored.Should().BeOfType<SingleDumpReportDocument>();
        ((SingleDumpReportDocument)restored).DumpPath.Should().Be("C:/dumps/test.dmp");
        // dedup diagnostics removed

        restored.HealthScorecard.Should().NotBeNull();
        restored.Domains.Should().HaveCount(1);
        restored.Domains![0].Domain.Should().Be("Memory");
        restored.Domains[0].Sections.Should().HaveCount(1);
        restored.CrossDomainInsights.Should().HaveCount(1);
        restored.Appendix.Should().NotBeNull();
        restored.Appendix!.AnalyzerRunSummary.Should().HaveCount(1);
    }

    [Fact]
    public void RoundTrip_PreservesSectionBlockPolymorphism()
    {
        SingleDumpReportDocument original = new()
        {
            DumpPath = "C:/test.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            Domains =
            [
                new ReportDomainSection("X", null,
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
                ])],
                [])
            ]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        IReadOnlyList<SectionBlock> blocks = restored!.Domains![0].Sections[0].Blocks;
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
    public void RoundTrip_PreservesTypedKeyMetrics()
    {
        AnalyzerDetailSection original = new(
            AnalyzerName: "DemoAnalyzer",
            DisplayTitle: "Demo",
            SortOrder: 1,
            Blocks: [],
            KeyMetrics: new System.Collections.Generic.Dictionary<string, MetricValue>
            {
                ["committed_bytes"] = new NumericMetricValue(2_220_929_024, MetricUnit.Bytes, "2.1 GB"),
                ["gc_mode"] = new EnumMetricValue("Server", "GCLatencyMode"),
                ["status"] = new TextMetricValue("Healthy")
            });

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalyzerDetailSection);
        json.Should().Contain("\"kind\":\"number\"");
        json.Should().Contain("\"kind\":\"enum\"");
        json.Should().Contain("\"kind\":\"text\"");

        AnalyzerDetailSection? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalyzerDetailSection);
        restored.Should().NotBeNull();
        restored.KeyMetrics.Should().HaveCount(3);
        restored.KeyMetrics.Should().ContainKey("committed_bytes");
        restored.KeyMetrics["committed_bytes"].Should().BeOfType<NumericMetricValue>().Which.Formatted.Should().Be("2.1 GB");
        restored.KeyMetrics.Should().ContainKey("gc_mode");
        restored.KeyMetrics["gc_mode"].Should().BeOfType<EnumMetricValue>().Which.EnumType.Should().Be("GCLatencyMode");
        restored.KeyMetrics.Should().ContainKey("status");
        restored.KeyMetrics["status"].Should().BeOfType<TextMetricValue>().Which.Value.Should().Be("Healthy");
    }

    [Fact]
    public void RoundTrip_PreservesTrendFields()
    {
        TrendReportDocument original = new()
        {
            ScoringModelVersion = "v1",
            GeneratedAtUtc = DateTime.UtcNow,
            TrendDumpCount = 3,
            TrendDumpPaths = ["C:/d1.dmp", "C:/d2.dmp", "C:/d3.dmp"],
            TrendStory = new TrendStoryRecord(
                Summary: "The first clear regression appears in Dump 2.",
                FirstMajorRegression: "Dump 2: Memory Analysis · memory.total.bytes.",
                LargestInflection: "Dump 3 is the largest inflection point.",
                TopWorseningDomains: ["Memory: 3 regressions"],
                LikelyCouplingHints: ["Memory and GC move together."],
                MetricReferences: ["Memory Analysis · memory.total.bytes"])
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().BeOfType<TrendReportDocument>();
        TrendReportDocument trend = (TrendReportDocument)restored!;
        trend.ScoringModelVersion.Should().Be("v1");
        trend.TrendDumpCount.Should().Be(3);
        trend.TrendDumpPaths.Should().HaveCount(3).And.Contain("C:/d2.dmp");
        trend.TrendStory.Should().NotBeNull();
        trend.TrendStory!.MetricReferences.Should().Contain("Memory Analysis · memory.total.bytes");
    }

    [Fact]
    public void RoundTrip_ExecutiveSummaryIsNullWhenAbsent()
    {
        SingleDumpReportDocument original = new() { DumpPath = "C:/t.dmp", GeneratedAtUtc = DateTime.UtcNow };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);

        json.Should().NotContain("\"executiveSummary\""); // WhenWritingNull suppresses it
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);
        restored!.ExecutiveSummary.Should().BeNull();
    }

    [Fact]
    public void JsonShape_UsesLowerCamelCase()
    {
        SingleDumpReportDocument original = new()
        {
            DumpPath = "C:/t.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1.0,
            Findings = [new FindingRecord("fp", "A", "B", "Info", "T", ["E"], "R", [])]
        };

        string json = JsonSerializer.Serialize(original, ReportJsonContext.Default.AnalysisReportDocument);

        json.Should().Contain("\"dumpPath\"");
        json.Should().Contain("\"elapsedSeconds\"");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().NotContain("\"findings\"");
        json.Should().NotContain("\"DumpPath\"");
        json.Should().NotContain("\"ElapsedSeconds\"");
    }
}
