using System.Linq;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests
{
    public class CorrelationBuilderTimelineTests
    {
        [Fact]
        public void BuildFrom_AppliesTimelineBoost()
        {
            // AnalyzerA shows strong metric jump at snapshot 1
            var findingA = new InsightFinding("AnalyzerA", "Cat", FindingSeverity.Warning, "TitleA", "E", "R", new[] { "regression" }, "fpA", 1.0, "u", 0.8, null);
            var findingB = new InsightFinding("AnalyzerB", "Cat", FindingSeverity.Warning, "TitleB", "E", "R", new[] { "regression" }, "fpB", 1.0, "u", 0.7, null);

            var snap0 = new AnalysisSnapshot(0, "dump0", new AnalyzerRunResult[0], new[] { findingA }, new System.Collections.Generic.Dictionary<string, AnalyzerDomainResult>(System.StringComparer.Ordinal), System.DateTime.UtcNow, null);
            var snap1 = new AnalysisSnapshot(1, "dump1", new AnalyzerRunResult[0], new[] { findingA, findingB }, new System.Collections.Generic.Dictionary<string, AnalyzerDomainResult>(System.StringComparer.Ordinal), System.DateTime.UtcNow, null);

            var tlPoint = new MetricTimelinePoint("m", "u", MetricTrendDirection.HigherIsWorse, new double[] { 10.0, 40.0 }, null);
            var timelineA = new AnalyzerMetricTimeline("AnalyzerA", new[] { tlPoint });

            var trendData = new TrendReportData(
                Steps: new[] { new AnalyzerTrendResult[0] },
                Overall: new AnalyzerTrendResult[0],
                NewLeakSignalsByAnalyzer: new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<NewLeakSignal>>(System.StringComparer.Ordinal),
                Timeline: new[] { timelineA },
                ScopedTimeline: System.Array.Empty<AnalyzerMetricTimeline>(),
                Snapshots: new[] { snap0, snap1 },
                NewFindings: System.Array.Empty<InsightFinding>(),
                PersistentFindings: System.Array.Empty<InsightFinding>(),
                ResolvedFindings: System.Array.Empty<InsightFinding>());

            var events = CorrelationBuilder.BuildFrom(trendData);
            events.Should().NotBeEmpty();
            var evt = events.First();
            evt.Confidence.Should().BeGreaterThan(0.8);
            evt.Rationale.Should().Contain("temporal metric deltas");
        }
    }
}
