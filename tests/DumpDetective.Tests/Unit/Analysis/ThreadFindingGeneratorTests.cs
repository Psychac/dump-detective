using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ThreadFindingGeneratorTests
{
    private static ThreadDomainResult BuildResult(
        int aliveThreadCount = 10,
        int blockedThreadCount = 0,
        bool finalizerThreadBlocked = false,
        int finalizerLockCount = 0,
        uint? finalizerManagedThreadId = null,
        double blockedThreadRatio = 0.0,
        IReadOnlyList<NameCountEntry>? topActiveThreadHotspots = null,
        int maxAsyncChainDepth = 0,
        int asyncChainThreadCount = 0) =>
        new ThreadDomainResult(
            TotalThreadCount: aliveThreadCount,
            AliveThreadCount: aliveThreadCount,
            InactiveThreadCount: 0,
            GcThreadCount: 0,
            BlockedThreadCount: blockedThreadCount,
            LockHoldingThreadCount: 0,
            ThreadsWithActiveExceptionsCount: 0,
            BackgroundThreadCount: 0,
            WaitPatternBreakdown: new Dictionary<string, int>(),
            TopActiveThreadHotspots: topActiveThreadHotspots ?? [new NameCountEntry("MyApp.Worker.Run()", aliveThreadCount)],
            FinalizerThreadBlocked: finalizerThreadBlocked,
            FinalizerLockCount: finalizerLockCount,
            FinalizerManagedThreadId: finalizerManagedThreadId,
            BlockedThreadRatio: blockedThreadRatio,
            MaxAsyncChainDepth: maxAsyncChainDepth,
            AsyncChainThreadCount: asyncChainThreadCount);

    [Fact]
    public void Generate_HealthyState_OnlyEmitsSummaryFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult();

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Be("Thread-state triage summary");
    }

    [Fact]
    public void Generate_FinalizerBlocked_EmitsCriticalFinalizerFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(finalizerThreadBlocked: true, finalizerLockCount: 2, finalizerManagedThreadId: 7);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("finalizer")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.Evidence.Should().Contain("managed thread 7");
    }

    [Fact]
    public void Generate_HighBlockedRatio_EmitsCriticalStarvationFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(aliveThreadCount: 10, blockedThreadCount: 8, blockedThreadRatio: 0.8);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("starvation")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.Tags.Should().Contain("deadlock");
    }

    [Fact]
    public void Generate_BlockedRatioAtThreshold_DoesNotEmitStarvationFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(aliveThreadCount: 10, blockedThreadCount: 7, blockedThreadRatio: 0.70);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("starvation"));
    }

    [Fact]
    public void Generate_ZeroActiveThreads_EmitsCriticalHangFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(aliveThreadCount: 5, topActiveThreadHotspots: []);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("hang")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_NoAliveThreads_DoesNotEmitZeroActiveThreadsFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(aliveThreadCount: 0, topActiveThreadHotspots: []);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("hang"));
    }

    [Fact]
    public void Generate_DeepAsyncChain_EmitsWarningFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(maxAsyncChainDepth: 15, asyncChainThreadCount: 3);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("continuation-chain")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("15");
    }

    [Fact]
    public void Generate_AsyncChainAtThreshold_DoesNotEmitFinding()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(maxAsyncChainDepth: 10);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("continuation-chain"));
    }

    [Fact]
    public void Generate_AllConditionsTriggered_EmitsAllFiveFindings()
    {
        var gen = new ThreadFindingGenerator();
        var result = BuildResult(
            aliveThreadCount: 10,
            blockedThreadCount: 8,
            blockedThreadRatio: 0.8,
            finalizerThreadBlocked: true,
            finalizerLockCount: 1,
            topActiveThreadHotspots: [],
            maxAsyncChainDepth: 20,
            asyncChainThreadCount: 4);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(5);
    }
}
