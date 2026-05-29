using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class TrendSnapshotSectionComposerTests
{
    [Fact]
    public void Build_ShouldIncludeTypedAnalyzerSectionSlots_InPerDumpDetails()
    {
        AnalyzerDetailSection analyzerSection = new(
            AnalyzerName: "DemoAnalyzer",
            DisplayTitle: "Demo Analyzer",
            SortOrder: 10,
            Blocks: [],
            KeyMetrics:
            [
                new SectionKeyMetric("Metric A", "123", 123)
            ],
            Tables:
            [
                new SectionTable(
                    Title: "Top Items",
                    Headers: ["Name", "Count"],
                    Rows:
                    [
                        new TableRow([new TableCell("Alpha"), new TableCell("42")])
                    ])
            ],
            LeadFinding: new SectionLeadFinding(
                Severity: "Warning",
                Title: "Lead issue",
                Evidence: "Evidence text",
                Recommendation: "Fix it",
                ConfidenceSymbol: "●●●○",
                ConfidenceScore: 0.7,
                Caveats: []),
            Provenance: new SectionProvenance(
                Analyzer: "DemoAnalyzer",
                Status: "Success",
                DurationMs: 12,
                ObjectScanCount: 100,
                CacheHits: 10,
                CacheMisses: 2));

        AnalyzerDetailSection composed = TrendSnapshotSectionComposer.Build(
            dumpPath: "C:/dumps/d1.dmp",
            generatedAtUtc: DateTime.UtcNow,
            findings: [],
            incidentContext: null,
            sections: [analyzerSection],
            dumpIndex: 0,
            totalDumps: 2,
            snapshot: null,
            baseline: null);

        composed.Blocks.OfType<CollapsibleSectionBeginBlock>().Should().ContainSingle(b => b.Title == "Demo Analyzer");
        composed.Blocks.OfType<HeadingBlock>().Any(h => h.Text == "Lead Finding [Warning]").Should().BeTrue();
        composed.Blocks.OfType<MetricBlock>().Any(m => m.Label == "Metric A" && m.Value == "123").Should().BeTrue();
        composed.Blocks.OfType<TableBlock>().Any(t => t.Caption == "Top Items" && t.Rows.Count == 1).Should().BeTrue();
        composed.Blocks.OfType<HeadingBlock>().Any(h => h.Text == "Provenance").Should().BeTrue();
    }
}
