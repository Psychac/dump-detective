using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

/// <summary>
/// Unit tests for P1.2: ExplainableScoringEngine — reproducible, weighted, contributor-backed scores.
/// </summary>
public sealed class ExplainableScoringEngineTests
{
    // ── No findings ───────────────────────────────────────────────────────────

    [Fact]
    public void ComputeScores_NoFindings_AllScoresZeroWithZeroConfidence()
    {
        var (leak, gc, thread) = ExplainableScoringEngine.ComputeScores([]);

        leak.Score.Should().Be(0);
        gc.Score.Should().Be(0);
        thread.Score.Should().Be(0);

        leak.Confidence.Should().Be(0.0);
        gc.Confidence.Should().Be(0.0);
        thread.Confidence.Should().Be(0.0);

        leak.Contributors.Should().BeEmpty();
        gc.Contributors.Should().BeEmpty();
        thread.Contributors.Should().BeEmpty();
    }

    // ── Score dimensions are isolated ─────────────────────────────────────────

    [Fact]
    public void ComputeScores_LeakCritical_OnlyLeakScoreNonZero()
    {
        var findings = new[]
        {
            F("LeakAnalyzer", "Leak", "Critical", "Large heap growth"),
        };

        var (leak, gc, thread) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(40);
        gc.Score.Should().Be(0);
        thread.Score.Should().Be(0);
    }

    [Fact]
    public void ComputeScores_GcCritical_OnlyGcScoreNonZero()
    {
        var findings = new[]
        {
            F("GcAnalyzer", "GC", "Critical", "High gen2 collection rate"),
        };

        var (leak, gc, thread) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(0);
        gc.Score.Should().Be(40);
        thread.Score.Should().Be(0);
    }

    [Fact]
    public void ComputeScores_ThreadingWarning_OnlyThreadScoreNonZero()
    {
        var findings = new[]
        {
            F("ThreadAnalyzer", "Threading", "Warning", "Thread starvation detected"),
        };

        var (leak, gc, thread) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(0);
        gc.Score.Should().Be(0);
        thread.Score.Should().Be(20);
    }

    // ── Score caps at 100 ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeScores_ManyLeakCriticals_ScoreCapAt100()
    {
        // 3 Critical findings × 40 pts = 120 → capped to 100
        var findings = new[]
        {
            F("LeakAnalyzer", "Leak", "Critical", "Growth A"),
            F("LeakAnalyzer", "Leak", "Critical", "Growth B"),
            F("LeakAnalyzer", "Memory", "Critical", "Growth C"),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(100);
        leak.Contributors.Should().HaveCount(3);
    }

    // ── Severity point mapping ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Critical", 40)]
    [InlineData("Warning", 20)]
    [InlineData("Info", 5)]
    public void ComputeScores_SeverityPoints_MappedCorrectly(string severity, int expectedPoints)
    {
        var findings = new[]
        {
            F("LeakAnalyzer", "Leak", severity, "Test finding"),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(expectedPoints);
        leak.Contributors.Should().HaveCount(1);
        leak.Contributors[0].Points.Should().Be(expectedPoints);
    }

    // ── Confidence weighting ──────────────────────────────────────────────────

    [Fact]
    public void ComputeScores_WithConfidenceScore_PointsAreWeighted()
    {
        // Critical = 40 pts × 0.5 confidence = 20 pts
        var findings = new[]
        {
            FWithConfidence("LeakAnalyzer", "Leak", "Critical", "Partial evidence", 0.5),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(20);
        leak.Contributors[0].Points.Should().Be(20);
        leak.Contributors[0].Detail.Should().Contain("50%");
    }

    [Fact]
    public void ComputeScores_WithoutConfidenceScore_HeuristicConfidenceUsed()
    {
        var findings = new[]
        {
            F("LeakAnalyzer", "Leak", "Warning", "No confidence field"),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        // No confidence field → heuristic 0.70
        leak.Confidence.Should().Be(0.70);
        leak.Contributors[0].Detail.Should().BeNull();
    }

    [Fact]
    public void ComputeScores_MixedConfidence_AveragesKnownValues()
    {
        var findings = new[]
        {
            FWithConfidence("LeakAnalyzer", "Leak", "Warning", "Confident finding", 0.80),
            FWithConfidence("LeakAnalyzer", "Leak", "Warning", "Less confident",    0.60),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        // Average confidence = (0.80 + 0.60) / 2 = 0.70
        leak.Confidence.Should().Be(0.70);
    }

    // ── Category matching is case-insensitive ─────────────────────────────────

    [Fact]
    public void ComputeScores_CategoryMatchIsCaseInsensitive()
    {
        var findings = new[]
        {
            F("LeakAnalyzer", "LEAK",   "Critical", "Upper case"),
            F("LeakAnalyzer", "memory", "Warning",  "Lower case"),
        };

        var (leak, _, _) = ExplainableScoringEngine.ComputeScores(findings);

        leak.Score.Should().Be(60); // 40 + 20
        leak.Contributors.Should().HaveCount(2);
    }

    // ── Breakdown dimensions are labeled correctly ────────────────────────────

    [Fact]
    public void ComputeScores_BreakdownDimensions_AreNamed()
    {
        var (leak, gc, thread) = ExplainableScoringEngine.ComputeScores([]);

        leak.Dimension.Should().Be("Leak");
        gc.Dimension.Should().Be("GcPressure");
        thread.Dimension.Should().Be("ThreadContention");
    }

    // ── ReportSerializer integration: ScoreBreakdowns populated ──────────────

    [Fact]
    public void ReportSerializer_ScoreBreakdowns_PopulatedInExecutiveSummary()
    {
        var finding = new InsightFinding(
            Analyzer: "LeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "Heap leak detected",
            Evidence: "Top type: byte[] 200MB",
            Recommendation: "Investigate retention chain",
            Tags: ["memory"],
            Fingerprint: "leak-001");

        var run = new AnalyzerRunResult(
            AnalyzerName: "LeakAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(50),
            Result: null,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [finding]);

        var doc = new ReportSerializer().Serialize("dump.dmp", [run], TimeSpan.FromSeconds(1), []);

        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.LeakLikelihoodScore.Should().Be(40);
        doc.ExecutiveSummary!.ScoreBreakdowns.Should().NotBeNull();
        doc.ExecutiveSummary!.ScoreBreakdowns!.Should().HaveCount(3);

        var leakBreakdown = doc.ExecutiveSummary.ScoreBreakdowns!.First(b => b.Dimension == "Leak");
        leakBreakdown.Score.Should().Be(40);
        leakBreakdown.Contributors.Should().HaveCount(1);
        leakBreakdown.Contributors[0].Source.Should().Be("LeakAnalyzer");
        leakBreakdown.Contributors[0].Points.Should().Be(40);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FindingRecord F(string analyzer, string category, string severity, string title) =>
        new(Analyzer: analyzer,
            Category: category,
            Severity: severity,
            Title: title,
            Evidence: string.Empty,
            Recommendation: string.Empty,
            Tags: [],
            Fingerprint: $"{analyzer}-{title}");

    private static FindingRecord FWithConfidence(string analyzer, string category, string severity, string title, double confidence) =>
        F(analyzer, category, severity, title) with { ConfidenceScore = confidence };
}
