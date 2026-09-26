using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ReportDomainProjectorTests
{
    [Fact]
    public void CrossAnalyzerInsight_WithoutCrossAnalyzerTag_AppearsOnlyInCrossDomainInsights()
    {
        FindingRecord insight = MakeFinding("InsightEngine", "Memory", "Large finalizer queue backlog detected");
        FindingRecord analyzerFinding = MakeFinding("Memory Analysis", "Memory", "High memory pressure score");
        IReadOnlyList<AnalyzerDetailSection> sections =
            [new AnalyzerDetailSection("Memory Analysis", "Memory Analysis", 0, [], Domain: "Memory")];

        IReadOnlyList<ReportDomainSection> domains = ReportDomainProjector.BuildDomainSections(sections, [insight, analyzerFinding]);
        IReadOnlyList<FindingRecord> crossDomain = ReportDomainProjector.BuildCrossDomainInsights([insight, analyzerFinding]);

        domains.SelectMany(d => d.DomainInsights).Should().ContainSingle().Which.Should().BeSameAs(analyzerFinding);
        crossDomain.Should().ContainSingle().Which.Should().BeSameAs(insight);
    }

    private static FindingRecord MakeFinding(string analyzer, string category, string title) => new(
        Id: title,
        Analyzer: analyzer,
        Category: category,
        Severity: "Warning",
        Title: title,
        Details: ["detail"],
        Recommendation: "recommendation",
        Tags: ["memory"]);
}
