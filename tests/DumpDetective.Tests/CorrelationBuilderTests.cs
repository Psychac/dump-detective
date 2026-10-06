using DumpDetective.Sdk.Synthesis;
using System.Linq;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using FluentAssertions;
using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests
{
    public class CorrelationBuilderTests
    {
        [Fact]
        public void BuildFrom_ReturnsEmpty_WhenNoSnapshots()
        {
            var trendData = new TrendReportData(
                Steps: new[] { new AnalyzerTrendResult[0] },
                Overall: new AnalyzerTrendResult[0],
                Timeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                ScopedTimeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                Snapshots: System.Array.Empty<AnalysisSnapshot>(),
                NewFindings: System.Array.Empty<Claim>(),
                PersistentFindings: System.Array.Empty<Claim>(),
                ResolvedFindings: System.Array.Empty<Claim>(),
                SeverityChanges: System.Array.Empty<DumpDetective.Reporting.Trend.FindingSeverityChange>());

            var events = CorrelationBuilder.BuildFrom(trendData);
            events.Should().BeEmpty();
        }

        [Fact]
        public void BuildFrom_FindsCooccurrenceEvent()
        {
            var findingA = ClaimFactory.Create("AnalyzerA", "Cat", Severity.Warning, "TitleA", "E", "R", new[] { "regression" }, "fpA", 1.0, "u", 0.8, null);
            var findingB = ClaimFactory.Create("AnalyzerB", "Cat", Severity.Warning, "TitleB", "E", "R", new[] { "regression" }, "fpB", 2.0, "u", 0.7, null);

            var snapshot = new AnalysisSnapshot(0, "dumpA", new AnalyzerRunResult[0], new[] { findingA, findingB }, new System.Collections.Generic.Dictionary<string, AnalyzerDomainResult>(System.StringComparer.Ordinal), System.DateTime.UtcNow, null);

            var trendData = new TrendReportData(
                Steps: new[] { new AnalyzerTrendResult[0] },
                Overall: new AnalyzerTrendResult[0],
                Timeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                ScopedTimeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                Snapshots: new[] { snapshot },
                NewFindings: System.Array.Empty<Claim>(),
                PersistentFindings: System.Array.Empty<Claim>(),
                ResolvedFindings: System.Array.Empty<Claim>(),
                SeverityChanges: System.Array.Empty<DumpDetective.Reporting.Trend.FindingSeverityChange>());

            var events = CorrelationBuilder.BuildFrom(trendData);
            events.Should().NotBeEmpty();
            events.First().Domains.Should().Contain(new[] { "AnalyzerA", "AnalyzerB" });
        }
    }
}
