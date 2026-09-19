using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class TrendAnalyzerTests
{
    // ── MetricDelta.GrowthRatePercent ─────────────────────────────────────────

    [Fact]
    public void GrowthRatePercent_ReturnsZero_WhenBaselineIsZero()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 0.0, 100.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.GrowthRatePercent.Should().Be(0.0);
    }

    [Fact]
    public void GrowthRatePercent_Returns100_WhenCurrentIsDoubleBaseline()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 50.0, 100.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.GrowthRatePercent.Should().BeApproximately(100.0, 0.001);
    }

    [Fact]
    public void GrowthRatePercent_ReturnsNegative_WhenCurrentLessThanBaseline()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 60.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.GrowthRatePercent.Should().BeApproximately(-40.0, 0.001);
    }

    // ── MetricDelta.Severity ─────────────────────────────────────────────────

    [Fact]
    public void Severity_IsNone_WhenNotARegression()
    {
        // HigherIsWorse but delta is negative (improvement)
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 80.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.Severity.Should().Be(RegressionSeverity.None);
    }

    [Fact]
    public void Severity_IsNone_WhenDirectionIsNeutral()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 200.0, "count", MetricTrendDirection.Neutral);
        delta.Severity.Should().Be(RegressionSeverity.None);
    }

    [Fact]
    public void Severity_IsMinor_WhenRegressionBelow10Pct()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 107.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.Severity.Should().Be(RegressionSeverity.Minor);
    }

    [Fact]
    public void Severity_IsModerate_WhenRegressionBetween10And50Pct()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 130.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.Severity.Should().Be(RegressionSeverity.Moderate);
    }

    [Fact]
    public void Severity_IsSevere_WhenRegressionOver50Pct()
    {
        var delta = MetricDeltaHelper_Compute("k", null, 100.0, 200.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.Severity.Should().Be(RegressionSeverity.Severe);
    }

    [Fact]
    public void Severity_IsModerate_WhenBaselineIsZeroAndCurrentPositive()
    {
        // DeltaPercent is null when baseline=0; severity defaults to Moderate
        var delta = MetricDeltaHelper_Compute("k", null, 0.0, 500.0, "bytes", MetricTrendDirection.HigherIsWorse);
        delta.Severity.Should().Be(RegressionSeverity.Moderate);
    }

    [Fact]
    public void ExtractTimeline_IncludesOnlyUnscopedMetrics()
    {
        AnalysisSnapshot first = MakeSnapshot(0, "Scoped Test Analyzer", new ScopedTestDomainResult(10, 4, 7));
        AnalysisSnapshot second = MakeSnapshot(1, "Scoped Test Analyzer", new ScopedTestDomainResult(12, 5, 9));

        TrendAnalyzer analyzer = new([new ScopedTestTrendComparer()]);

        IReadOnlyList<AnalyzerMetricTimeline> timeline = analyzer.ExtractTimeline([first, second]);

        timeline.Should().ContainSingle();
        AnalyzerMetricTimeline analyzerTimeline = timeline[0];
        analyzerTimeline.Points.Should().ContainSingle();
        analyzerTimeline.Points[0].Key.Should().Be("metric.total");
        analyzerTimeline.Points[0].Scope.Should().BeNull();
        analyzerTimeline.Points[0].Values.Should().Equal([10, 12]);
    }

    [Fact]
    public void ExtractScopedTimeline_PreservesScopedEntityRowsWithoutCollision()
    {
        AnalysisSnapshot first = MakeSnapshot(0, "Scoped Test Analyzer", new ScopedTestDomainResult(10, 4, 7));
        AnalysisSnapshot second = MakeSnapshot(1, "Scoped Test Analyzer", new ScopedTestDomainResult(12, 5, 9));

        TrendAnalyzer analyzer = new([new ScopedTestTrendComparer()]);

        IReadOnlyList<AnalyzerMetricTimeline> scopedTimeline = analyzer.ExtractScopedTimeline([first, second]);

        scopedTimeline.Should().ContainSingle();
        AnalyzerMetricTimeline analyzerTimeline = scopedTimeline[0];
        analyzerTimeline.Points.Should().HaveCount(2);

        MetricTimelinePoint entityA = analyzerTimeline.Points.Single(p => p.Scope == "EntityA");
        entityA.Key.Should().Be("metric.entity");
        entityA.Values.Should().Equal([4, 5]);

        MetricTimelinePoint entityB = analyzerTimeline.Points.Single(p => p.Scope == "EntityB");
        entityB.Key.Should().Be("metric.entity");
        entityB.Values.Should().Equal([7, 9]);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static MetricDelta MetricDeltaHelper_Compute(
        string key, string? scope,
        double baseline, double current,
        string unit, MetricTrendDirection direction)
    {
        double delta = current - baseline;
        double? deltaPercent = Math.Abs(baseline) > double.Epsilon
            ? delta * 100.0 / baseline
            : null;
        return new MetricDelta(key, scope, baseline, current, delta, deltaPercent, unit, direction);
    }

    private static AnalysisSnapshot MakeSnapshot(
        int index, string analyzerName, AnalyzerDomainResult domainResult)
    {
        return new AnalysisSnapshot(
            Index: index,
            DumpPath: $"dump{index}.dmp",
            Runs: [],
            Findings: [],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal)
            {
                [analyzerName] = domainResult
            },
            GeneratedAtUtc: DateTime.UtcNow);
    }

    private sealed record ScopedTestDomainResult(double Total, double EntityA, double EntityB) : AnalyzerDomainResult;

    private sealed class ScopedTestTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Scoped Test Analyzer";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ScopedTestDomainResult r)
                return [];

            return
            [
                new AnalyzerMetric("metric.total", null, r.Total, "items", MetricTrendDirection.HigherIsWorse),
                new AnalyzerMetric("metric.entity", "EntityA", r.EntityA, "items", MetricTrendDirection.HigherIsWorse),
                new AnalyzerMetric("metric.entity", "EntityB", r.EntityB, "items", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
            => [];
    }
}
