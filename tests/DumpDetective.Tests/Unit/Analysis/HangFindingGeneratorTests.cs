using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class HangFindingGeneratorTests
{
    private readonly HangFindingGenerator _gen = new();

    [Fact]
    public void Generate_WaitingPercentAtOrAboveEighty_EmitsCriticalFinding()
    {
        HangDomainResult result = BuildResult(waitingPercent: 85.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_WaitingPercentAtOrAboveFifty_EmitsWarningFinding()
    {
        HangDomainResult result = BuildResult(waitingPercent: 60.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_QueuedWorkItemsAboveThreshold_EmitsWarningFinding()
    {
        HangDomainResult result = BuildResult(waitingPercent: 5.0, queuedWorkItems: 600);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LowWaitingPercent_LowQueue_EmitsInfoFinding()
    {
        HangDomainResult result = BuildResult(waitingPercent: 5.0, queuedWorkItems: 10);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    private static HangDomainResult BuildResult(double waitingPercent, int queuedWorkItems = 0) => new(
        TotalAliveThreads: 100,
        WaitingThreadCount: (int)(100 * waitingPercent / 100.0),
        ThreadsHoldingLocks: 0,
        WaitingPercent: waitingPercent,
        WaitCategoryBreakdown: new Dictionary<string, int>(),
        TotalTaskContinuations: 0,
        QueuedWorkItems: queuedWorkItems,
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
        HealthScore: 80);
}
