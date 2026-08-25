using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class FinalizableObjectFindingGeneratorTests
{
    [Fact]
    public void Generate_NoCriticalFinalizerObjects_EmitsNoCriticalFinalizerFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(criticalFinalizerQueueCount: 0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("criticalfinalizerobject"));
    }

    [Fact]
    public void Generate_CriticalFinalizerObjectsBelowThreshold_EmitsNoCriticalFinalizerFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(criticalFinalizerQueueCount: 50);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("criticalfinalizerobject"));
    }

    [Fact]
    public void Generate_CriticalFinalizerObjectsAboveWarningThreshold_EmitsWarningFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(
            criticalFinalizerQueueCount: 500,
            criticalFinalizerQueueBytes: 4096,
            topCriticalFinalizerTypesByCount: [new QueueTypeStatistic("Microsoft.Win32.SafeHandles.SafeFileHandle", 500)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("criticalfinalizerobject")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("SafeFileHandle");
    }

    [Fact]
    public void Generate_CriticalFinalizerObjectsAboveCriticalThreshold_EmitsCriticalFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(
            criticalFinalizerQueueCount: 2_000,
            criticalFinalizerQueueBytes: 4096,
            topCriticalFinalizerTypesByCount: [new QueueTypeStatistic("System.Net.Sockets.SafeSocketHandle", 2_000)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("criticalfinalizerobject")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_DynamicResolverBelowInfoThreshold_EmitsNoDynamicResolverFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Reflection.Emit.DynamicResolver", 9)]);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("dynamic-method"));
    }

    [Fact]
    public void Generate_DynamicResolverAboveInfoThreshold_EmitsInfoFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Reflection.Emit.DynamicResolver", 10)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("dynamic-method")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_DynamicResolverAboveWarningThreshold_EmitsWarningFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Reflection.Emit.DynamicResolver", 100)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("dynamic-method")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_AbandonedThreadsAboveThreshold_EmitsThreadFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Threading.Thread", 50)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("thread-abandonment")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_UndisposedTimersAboveThreshold_EmitsTimerFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Threading.TimerHolder", 5)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("timer")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_AbandonedReaderWriterLocksAboveThreshold_EmitsWarningFinding()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("System.Threading.ReaderWriterLock", 3)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("reader-writer-lock")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoKnownPatternTypesInQueue_EmitsNoKnownPatternFindings()
    {
        var gen = new FinalizableObjectFindingGenerator();
        var result = BuildResult(topQueueTypesByCount: [new QueueTypeStatistic("MyApp.SomeUnrelatedType", 1_000)]);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("dynamic-method") || f.Tags.Contains("thread-abandonment") ||
                                           f.Tags.Contains("timer") || f.Tags.Contains("reader-writer-lock"));
    }

    private static FinalizableObjectDomainResult BuildResult(
        int criticalFinalizerQueueCount = 0,
        ulong criticalFinalizerQueueBytes = 0,
        IReadOnlyList<QueueTypeStatistic>? topCriticalFinalizerTypesByCount = null,
        IReadOnlyList<QueueTypeStatistic>? topQueueTypesByCount = null) =>
        new(
            TotalFinalizableObjects: 100,
            TotalFinalizableBytes: 10_000,
            Gen0Count: 50,
            Gen1Count: 30,
            Gen2Count: 20,
            LohCount: 0,
            FinalizerQueueCount: criticalFinalizerQueueCount > 0 ? criticalFinalizerQueueCount : 5,
            FinalizerQueueRetainedBytes: 0,
            IsRetainedEstimatePartial: false,
            HasUndisposedDisposableInQueue: false,
            CriticalFinalizerQueueCount: criticalFinalizerQueueCount,
            CriticalFinalizerQueueBytes: criticalFinalizerQueueBytes,
            TopFinalizableTypesByGen2Count: [],
            TopQueueTypesByCount: topQueueTypesByCount ?? [],
            TopCriticalFinalizerTypesByCount: topCriticalFinalizerTypesByCount ?? [],
            TopQueueEntriesByRetainedSize: []);
}
