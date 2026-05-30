using DumpDetective.Analysis.Insight;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class InsightEngineTests
{
    [Fact]
    public void Analyze_ShouldEmitWarning_WhenThreeAnalyzersFail()
    {
        InsightEngine engine = new();

        AnalyzerRunResult[] runs =
        [
            BuildRun("A", AnalyzerExecutionStatus.Failed),
            BuildRun("B", AnalyzerExecutionStatus.Failed),
            BuildRun("C", AnalyzerExecutionStatus.Failed),
            BuildRun("D", AnalyzerExecutionStatus.Success, new GenericAnalyzerDomainResult())
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Warning
            && f.Title.Contains("analyzer(s) failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCritical_WhenFatalExceptionTypePresent()
    {
        InsightEngine engine = new();

        CrashDomainResult crash = new(
            TotalExceptions: 3,
            ActiveExceptions: 0,
            ExceptionTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["System.OutOfMemoryException"] = 1,
                ["System.InvalidOperationException"] = 2,
            },
            ActiveExceptionTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal));

        AnalyzerRunResult[] runs =
        [
            BuildRun("Crash Analyzer", AnalyzerExecutionStatus.Success, crash)
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Critical
            && f.Title.Contains("Fatal exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCritical_WhenLohPressureExceedsThreshold()
    {
        InsightEngine engine = new();

        MemoryDomainResult memory = new(
            TotalBytes: 1_000,
            LohBytes: 450,
            LohPercent: 45.0,
            TotalObjects: 10,
            LohObjects: 1,
            LohThresholdBytes: 85_000,
            UniqueTypes: 3,
            TopTypes: []);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analyzer", AnalyzerExecutionStatus.Success, memory)
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Critical
            && f.Title.Contains("LOH", StringComparison.OrdinalIgnoreCase));
    }

    private static AnalyzerRunResult BuildRun(string analyzerName, AnalyzerExecutionStatus status, AnalyzerDomainResult? result = null)
        => new(
            AnalyzerName: analyzerName,
            Status: status,
            Duration: TimeSpan.Zero,
            Result: result,
            ErrorMessage: status == AnalyzerExecutionStatus.Failed ? "failed" : null,
            ErrorType: status == AnalyzerExecutionStatus.Failed ? nameof(InvalidOperationException) : null);
}
