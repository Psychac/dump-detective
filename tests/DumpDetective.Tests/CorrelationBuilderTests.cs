using System.Linq;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using FluentAssertions;
using Xunit;

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
                NewLeakSignalsByAnalyzer: new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<NewLeakSignal>>(System.StringComparer.Ordinal),
                Timeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                ScopedTimeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                Snapshots: System.Array.Empty<AnalysisSnapshot>(),
                NewFindings: System.Array.Empty<InsightFinding>(),
                PersistentFindings: System.Array.Empty<InsightFinding>(),
                ResolvedFindings: System.Array.Empty<InsightFinding>());

            var events = CorrelationBuilder.BuildFrom(trendData);
            events.Should().BeEmpty();
        }

        [Fact]
        public void BuildFrom_FindsCooccurrenceEvent()
        {
            var findingA = new InsightFinding("AnalyzerA", "Cat", FindingSeverity.Warning, "TitleA", "E", "R", new[] { "regression" }, "fpA", 1.0, "u", 0.8, null);
            var findingB = new InsightFinding("AnalyzerB", "Cat", FindingSeverity.Warning, "TitleB", "E", "R", new[] { "regression" }, "fpB", 2.0, "u", 0.7, null);

            var snapshot = new AnalysisSnapshot(0, "dumpA", new AnalyzerRunResult[0], new[] { findingA, findingB }, new System.Collections.Generic.Dictionary<string, AnalyzerDomainResult>(System.StringComparer.Ordinal), System.DateTime.UtcNow, null);

            var trendData = new TrendReportData(
                Steps: new[] { new AnalyzerTrendResult[0] },
                Overall: new AnalyzerTrendResult[0],
                NewLeakSignalsByAnalyzer: new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<NewLeakSignal>>(System.StringComparer.Ordinal),
                Timeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                ScopedTimeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                Snapshots: new[] { snapshot },
                NewFindings: System.Array.Empty<InsightFinding>(),
                PersistentFindings: System.Array.Empty<InsightFinding>(),
                ResolvedFindings: System.Array.Empty<InsightFinding>());

            var events = CorrelationBuilder.BuildFrom(trendData);
            events.Should().NotBeEmpty();
            events.First().Domains.Should().Contain(new[] { "AnalyzerA", "AnalyzerB" });
        }
    }
}
