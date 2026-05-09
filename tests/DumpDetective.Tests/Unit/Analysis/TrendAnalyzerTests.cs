using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend;
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

    // ── TrendAnalyzer.CompareAll — NewLeakSignals ────────────────────────────

    [Fact]
    public void CompareAll_PopulatesNewLeakSignals_WhenTypeAppearsInCurrent()
    {
        RetentionDomainResult baselineResult = new(
            FinalizerQueueCount: 0,
            HighlyReferencedObjectCount: 0,
            SkippedReferenceAddresses: 0,
            TopHighlyReferencedObjects: []);

        RetentionDomainResult currentResult = new(
            FinalizerQueueCount: 0,
            HighlyReferencedObjectCount: 1,
            SkippedReferenceAddresses: 0,
            TopHighlyReferencedObjects:
            [
                new HighlyReferencedObjectSnapshot(0x1000, "MyApp.CachedService", 512_000, 50)
            ]);

        var baselineSnap = MakeSnapshot(0, "Retention Analysis", baselineResult);
        var currentSnap = MakeSnapshot(1, "Retention Analysis", currentResult);

        TrendAnalyzer analyzer = new([new RetentionTrendComparer()]);
        var results = analyzer.CompareAll(baselineSnap, currentSnap);

        var leakResult = results.FirstOrDefault(r => r.AnalyzerName == "Retention Analysis");
        leakResult.Should().NotBeNull();
        leakResult!.NewLeakSignals.Should().ContainSingle(s => s.TypeName == "MyApp.CachedService");
    }

    [Fact]
    public void CompareAll_NoNewLeakSignals_WhenTypeExistedWithSimilarSize()
    {
        var existing = new HighlyReferencedObjectSnapshot(0x1000, "System.String", 100_000, 5);

        RetentionDomainResult baselineResult = new(0, 1, 0,
            TopHighlyReferencedObjects: [existing]);

        // current has same type with only a tiny increase — not a new signal
        RetentionDomainResult currentResult = new(0, 1, 0,
            TopHighlyReferencedObjects:
            [
                new HighlyReferencedObjectSnapshot(0x1000, "System.String", 101_000, 5)
            ]);

        var baselineSnap = MakeSnapshot(0, "Retention Analysis", baselineResult);
        var currentSnap = MakeSnapshot(1, "Retention Analysis", currentResult);

        TrendAnalyzer analyzer = new([new RetentionTrendComparer()]);
        var results = analyzer.CompareAll(baselineSnap, currentSnap);

        var leakResult = results.FirstOrDefault(r => r.AnalyzerName == "Retention Analysis");
        leakResult?.NewLeakSignals.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzerTrendResult_NewLeakSignals_DefaultsToEmpty()
    {
        var result = new AnalyzerTrendResult("SomeAnalyzer",
        [
            MetricDeltaHelper_Compute("m", null, 1.0, 2.0, "bytes", MetricTrendDirection.HigherIsWorse)
        ]);
        result.NewLeakSignals.Should().BeEmpty();
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
            Findings: [],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal)
            {
                [analyzerName] = domainResult
            },
            GeneratedAtUtc: DateTime.UtcNow);
    }

    // Minimal comparer stub so TrendAnalyzer can find the "Retention Analysis" entry
    private sealed class RetentionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Retention Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not RetentionDomainResult r) return [];
            return [new AnalyzerMetric("leak.highly.referenced", null, r.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse)];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not RetentionDomainResult b || current is not RetentionDomainResult c)
                return [];
            double delta = c.HighlyReferencedObjectCount - b.HighlyReferencedObjectCount;
            double? pct = Math.Abs(b.HighlyReferencedObjectCount) > double.Epsilon
                ? delta * 100.0 / b.HighlyReferencedObjectCount : null;
            return [new MetricDelta("leak.highly.referenced", null,
                b.HighlyReferencedObjectCount, c.HighlyReferencedObjectCount,
                delta, pct, "objects", MetricTrendDirection.HigherIsWorse)];
        }
    }
}
