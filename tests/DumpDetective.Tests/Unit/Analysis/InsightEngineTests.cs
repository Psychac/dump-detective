using DumpDetective.Analysis.Insight;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
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

    [Fact]
    public void Analyze_ShouldEmitCorrelation_WhenDominantClusterOverlapsHangWaitingThreads()
    {
        InsightEngine engine = new();

        ThreadStackClusterDomainResult clusters = new(
            AliveThreadCount: 10,
            UniqueClusters: 2,
            SingletonSignatures: 0,
            DiversityPercent: 20.0,
            TopClusterSignatures: ["sig-a"],
            TopClusters:
            [
                new ThreadClusterSnapshot(
                    Count: 8,
                    SampleOsThreadIds: [1, 2, 3, 4, 5],
                    Signature: "sig-a")
            ]);

        HangDomainResult hang = new(
            TotalAliveThreads: 10,
            WaitingThreadCount: 5,
            ThreadsHoldingLocks: 0,
            WaitingPercent: 50.0,
            WaitCategoryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            TotalTaskContinuations: 0,
            QueuedWorkItems: 0,
            TotalTasks: 0,
            PendingTasks: 0,
            FaultedTasks: 0,
            CanceledTasks: 0,
            RuntimeThreadPoolDataAvailable: false,
            RuntimeMinThreads: 0,
            RuntimeMaxThreads: 0,
            RuntimeActiveWorkerThreads: 0,
            RuntimeIdleWorkerThreads: 0,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeQueueLength: null,
            RuntimeCpuUtilization: 0,
            IsStarved: false,
            HealthScore: 50,
            TopWaitingThreads:
            [
                new WaitingThreadSnapshot(1, 1, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(2, 2, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(3, 3, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(4, 4, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
            ]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Thread Stack Signature Clustering", AnalyzerExecutionStatus.Success, clusters),
            BuildRun("Hang Analyzer", AnalyzerExecutionStatus.Success, hang),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("HangAnalyzer", StringComparison.OrdinalIgnoreCase)
            && f.Title.Contains("cluster", StringComparison.OrdinalIgnoreCase)
            && f.Evidence.Contains("Monitor.Wait", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ShouldNotEmitCorrelation_WhenClusterAndHangThreadsDoNotOverlap()
    {
        InsightEngine engine = new();

        ThreadStackClusterDomainResult clusters = new(
            AliveThreadCount: 10,
            UniqueClusters: 2,
            SingletonSignatures: 0,
            DiversityPercent: 20.0,
            TopClusterSignatures: ["sig-a"],
            TopClusters:
            [
                new ThreadClusterSnapshot(
                    Count: 8,
                    SampleOsThreadIds: [101, 102, 103, 104, 105],
                    Signature: "sig-a")
            ]);

        HangDomainResult hang = new(
            TotalAliveThreads: 10,
            WaitingThreadCount: 5,
            ThreadsHoldingLocks: 0,
            WaitingPercent: 50.0,
            WaitCategoryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            TotalTaskContinuations: 0,
            QueuedWorkItems: 0,
            TotalTasks: 0,
            PendingTasks: 0,
            FaultedTasks: 0,
            CanceledTasks: 0,
            RuntimeThreadPoolDataAvailable: false,
            RuntimeMinThreads: 0,
            RuntimeMaxThreads: 0,
            RuntimeActiveWorkerThreads: 0,
            RuntimeIdleWorkerThreads: 0,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeQueueLength: null,
            RuntimeCpuUtilization: 0,
            IsStarved: false,
            HealthScore: 50,
            TopWaitingThreads:
            [
                new WaitingThreadSnapshot(1, 1, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(2, 2, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
            ]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Thread Stack Signature Clustering", AnalyzerExecutionStatus.Success, clusters),
            BuildRun("Hang Analyzer", AnalyzerExecutionStatus.Success, hang),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f =>
            f.Title.Contains("HangAnalyzer", StringComparison.OrdinalIgnoreCase)
            && f.Title.Contains("cluster", StringComparison.OrdinalIgnoreCase));
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
