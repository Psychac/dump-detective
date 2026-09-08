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

    // ── TrendAnalyzer.ComputeNewLeakSignals ──────────────────────────────────

    [Fact]
    public void CompareAll_PopulatesNewLeakSignals_WhenTypeAppearsInCurrent()
    {
        DominatorDomainResult baselineResult = new(
            CandidateCount: 0,
            AnalyzedCount: 0,
            TotalEstimatedRetainedBytes: 0,
            TopDominatorTypes: [],
            HighlyReferencedObjectCount: 0,
            TopHighlyReferencedObjects: []);

        DominatorDomainResult currentResult = new(
            CandidateCount: 0,
            AnalyzedCount: 0,
            TotalEstimatedRetainedBytes: 0,
            TopDominatorTypes: [],
            HighlyReferencedObjectCount: 1,
            TopHighlyReferencedObjects:
            [
                new HighlyReferencedObjectSnapshot(0x1000, "MyApp.CachedService", 512_000, 50)
            ]);

        var baselineSnap = MakeSnapshot(0, "Dominator Analysis", baselineResult);
        var currentSnap = MakeSnapshot(1, "Dominator Analysis", currentResult);

        TrendAnalyzer analyzer = new([new DominatorTrendComparer()]);
        var signals = analyzer.ComputeNewLeakSignals(baselineSnap, currentSnap);

        signals.Should().ContainKey("Dominator Analysis");
        signals["Dominator Analysis"].Should().ContainSingle(s => s.TypeName == "MyApp.CachedService");
    }

    [Fact]
    public void CompareAll_NoNewLeakSignals_WhenTypeExistedWithSimilarSize()
    {
        var existing = new HighlyReferencedObjectSnapshot(0x1000, "System.String", 100_000, 5);

        DominatorDomainResult baselineResult = new(0, 0, 0, [],
            HighlyReferencedObjectCount: 1,
            TopHighlyReferencedObjects: [existing]);

        // current has same type with only a tiny increase — not a new signal
        DominatorDomainResult currentResult = new(0, 0, 0, [],
            HighlyReferencedObjectCount: 1,
            TopHighlyReferencedObjects:
            [
                new HighlyReferencedObjectSnapshot(0x1000, "System.String", 101_000, 5)
            ]);

        var baselineSnap = MakeSnapshot(0, "Dominator Analysis", baselineResult);
        var currentSnap = MakeSnapshot(1, "Dominator Analysis", currentResult);

        TrendAnalyzer analyzer = new([new DominatorTrendComparer()]);
        var signals = analyzer.ComputeNewLeakSignals(baselineSnap, currentSnap);

        signals.Should().NotContainKey("Dominator Analysis");
    }

    [Fact]
    public void ComputeNewLeakSignals_PopulatesLeakCandidateSignals_WhenCandidateAppearsInCurrent()
    {
        LeakCandidateDomainResult baselineResult = new(
            TotalCandidates: 0,
            TopCandidates: [],
            CandidatesByClass: new Dictionary<LeakClass, int>(),
            HeuristicOnly: true);

        LeakCandidateDomainResult currentResult = new(
            TotalCandidates: 1,
            TopCandidates:
            [
                new LeakCandidateRecord(
                    TypeName: "MyApp.CacheBucket",
                    TotalSize: 2_000_000,
                    InstanceCount: 100,
                    Gen2Pct: 95.0,
                    SuspicionScore: 85,
                    Severity: FindingSeverity.Warning,
                    Classification: LeakClass.CacheLeak,
                    RootKind: "StaticRoot",
                    IsFinalizable: false,
                    IsContainer: true,
                    ReferenceFieldRatio: 0.75)
            ],
            CandidatesByClass: new Dictionary<LeakClass, int> { [LeakClass.CacheLeak] = 1 },
            HeuristicOnly: true);

        var baselineSnap = MakeSnapshot(0, "Leak Candidate Analysis", baselineResult);
        var currentSnap = MakeSnapshot(1, "Leak Candidate Analysis", currentResult);

        TrendAnalyzer analyzer = new([new LeakCandidateTrendComparer()]);
        var signals = analyzer.ComputeNewLeakSignals(baselineSnap, currentSnap);

        signals.Should().ContainKey("Leak Candidate Analysis");
        signals["Leak Candidate Analysis"].Should().ContainSingle(s => s.TypeName == "MyApp.CacheBucket");
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
