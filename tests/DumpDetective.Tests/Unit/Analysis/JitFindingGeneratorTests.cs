using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class JitFindingGeneratorTests
{
    [Fact]
    public void Generate_NoSignals_ReturnsOverviewOnly()
    {
        var gen = new JitFindingGenerator();
        var result = BuildResult(totalJitHeapBytes: 100 * 1024 * 1024, unmanagedFrames: 10, managedFrames: 200, tieredMethods: 0, topMethods: []);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Contain("overview");
        findings[0].Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_WithSignals_ReturnsOverviewAndAllSignals()
    {
        var gen = new JitFindingGenerator();
        var topMethods = new List<JitMethodSnapshot>
        {
            new("Foo.Bar()", "Foo", 0x1000, 70_000, 10_000, false, false)
        };
        var result = BuildResult(totalJitHeapBytes: 800 * 1024 * 1024, unmanagedFrames: 400, managedFrames: 200, tieredMethods: 250, topMethods: topMethods);

        var findings = gen.Generate(result);

        // Should emit overview + 4 concurrent signals (heap bloat, unmanaged ratio, tiered, large methods)
        findings.Should().HaveCount(5);
        findings[0].Title.Should().Contain("overview");
        findings.Should().ContainSingle(f => f.Title.Contains("unusually large") && f.Severity == FindingSeverity.Warning);
        findings.Should().ContainSingle(f => f.Title.Contains("High unmanaged") && f.Severity == FindingSeverity.Info);
        findings.Should().ContainSingle(f => f.Title.Contains("Tiered compilation") && f.Severity == FindingSeverity.Info);
        findings.Should().ContainSingle(f => f.Title.Contains("Large JIT-compiled") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_JitHeapBloat_OverviewSeverityEscalatesToWarning()
    {
        var gen = new JitFindingGenerator();
        var result = BuildResult(totalJitHeapBytes: 600 * 1024 * 1024, unmanagedFrames: 0, managedFrames: 100, tieredMethods: 0, topMethods: []);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title.Contains("overview")).Which.Severity.Should().Be(FindingSeverity.Warning);
        findings.Should().ContainSingle(f => f.Title == "JIT code heap unusually large" && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_DeepThreadStack_EmitsWarningFinding()
    {
        var gen = new JitFindingGenerator();
        var result = BuildResult(
            totalJitHeapBytes: 1024, unmanagedFrames: 0, managedFrames: 10, tieredMethods: 0, topMethods: [],
            maxThreadFrameDepth: 600);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title.Contains("deep thread stack") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_DynamicMethodFrames_EmitsInfoFinding()
    {
        var gen = new JitFindingGenerator();
        var result = BuildResult(
            totalJitHeapBytes: 1024, unmanagedFrames: 0, managedFrames: 10, tieredMethods: 0, topMethods: [],
            dynamicMethodFrameCount: 5);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title.Contains("Dynamic codegen") && f.Severity == FindingSeverity.Info);
    }

    private static JitDomainResult BuildResult(
        ulong totalJitHeapBytes,
        int unmanagedFrames,
        int managedFrames,
        int tieredMethods,
        IReadOnlyList<JitMethodSnapshot> topMethods,
        int maxThreadFrameDepth = 0,
        int dynamicMethodFrameCount = 0)
    {
        return new JitDomainResult(
            TotalJitHeapBytes: totalJitHeapBytes,
            JitManagerCount: 3,
            ActiveMethodsOnStacks: managedFrames,
            DistinctMethodsOnStacks: managedFrames,
            TopLargestMethods: topMethods,
            TopActiveFrameTypes: [],
            TopActiveModulesByFrameHits: [],
            UnmanagedFrameCount: unmanagedFrames,
            ManagedFrameCount: managedFrames,
            ReadyToRunFrameCount: 0,
            DynamicMethodFrameCount: dynamicMethodFrameCount,
            TieredMethodCount: tieredMethods,
            MaxThreadFrameDepth: maxThreadFrameDepth,
            MaxThreadFrameDepthOSThreadId: 0,
            LargeMethodThresholdBytes: 64 * 1024);
    }
}
