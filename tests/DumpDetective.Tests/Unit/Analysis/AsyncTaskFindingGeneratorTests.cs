using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AsyncTaskFindingGeneratorTests
{
    [Fact]
    public void Generate_NoSignals_ReturnsEmpty()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 100, pending: 100, faulted: 0, orphaned: 0, maxDepth: 5, avgDepth: 2.0);

        var findings = gen.Generate(result);

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Generate_SingleSignal_ReturnsOnlyThatSignal()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 200, pending: 120, faulted: 0, orphaned: 12, maxDepth: 4, avgDepth: 1.2);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Contain("Orphaned tasks detected");
    }

    [Fact]
    public void Generate_MultipleSignals_ReturnsAggregatePlusTopDetail()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 2000, pending: 1800, faulted: 120, orphaned: 420, maxDepth: 17, avgDepth: 8.4);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(2);
        findings[0].Title.Should().Contain("risk cluster");
        findings[1].Title.Should().Contain("Orphaned tasks detected");
        findings[0].Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_AllSignals_StillCapsAtTwoFindings()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 10000, pending: 7000, faulted: 300, orphaned: 500, maxDepth: 25, avgDepth: 12.5);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(2);
    }

    [Fact]
    public void Generate_PendingVtsGen2BelowInfoThreshold_ReturnsInfoSeverity()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 100, pending: 0, faulted: 0, orphaned: 0, maxDepth: 0, avgDepth: 0)
            with { TotalValueTaskSources = 50, PendingValueTaskSources = 10, PendingVtsGen2Count = 5 };

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Contain("Pending IValueTaskSource instances in Gen2/LOH");
        findings[0].Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_PendingVtsGen2AtOrAboveWarningThreshold_ReturnsWarningSeverity()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 100, pending: 0, faulted: 0, orphaned: 0, maxDepth: 0, avgDepth: 0)
            with { TotalValueTaskSources = 100, PendingValueTaskSources = 40, PendingVtsGen2Count = 20 };

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoPendingVtsGen2_NoVtsSignal()
    {
        var gen = new AsyncTaskFindingGenerator();
        var result = BuildResult(total: 100, pending: 0, faulted: 0, orphaned: 0, maxDepth: 0, avgDepth: 0)
            with { TotalValueTaskSources = 50, PendingValueTaskSources = 50, PendingVtsGen2Count = 0 };

        var findings = gen.Generate(result);

        findings.Should().BeEmpty();
    }

    private static AsyncTaskDomainResult BuildResult(
        int total,
        int pending,
        int faulted,
        int orphaned,
        int maxDepth,
        double avgDepth)
    {
        int completed = total - pending;
        if (completed < 0) completed = 0;

        return new AsyncTaskDomainResult(
            TotalTasks: total,
            PendingTasks: pending,
            RunningTasks: pending,
            FaultedTasks: faulted,
            CanceledTasks: 0,
            CompletedTasks: completed,
            OrphanedTasks: orphaned,
            TotalTaskContinuations: total * 2,
            MaxContinuationDepth: maxDepth,
            AvgContinuationDepth: avgDepth,
            TopPendingTaskTypes: [],
            TopFaultedTaskTypes: [],
            TopContinuationTypes: [],
            TopOrphanedTasks: [],
            TopDeepestChains: []);
    }
}
