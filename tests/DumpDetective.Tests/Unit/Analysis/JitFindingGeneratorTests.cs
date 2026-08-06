using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
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
    }

    [Fact]
    public void Generate_WithSignals_ReturnsOverviewAndTopDetail()
    {
        var gen = new JitFindingGenerator();
        var topMethods = new List<JitMethodSnapshot>
        {
            new("Foo.Bar()", "Foo", 0x1000, 70_000, 10_000, false)
        };
        var result = BuildResult(totalJitHeapBytes: 800 * 1024 * 1024, unmanagedFrames: 400, managedFrames: 200, tieredMethods: 250, topMethods: topMethods);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(2);
        findings[0].Title.Should().Contain("overview");
        findings[1].Title.Should().Contain("unusually large");
    }

    private static JitDomainResult BuildResult(
        ulong totalJitHeapBytes,
        int unmanagedFrames,
        int managedFrames,
        int tieredMethods,
        IReadOnlyList<JitMethodSnapshot> topMethods)
    {
        return new JitDomainResult(
            TotalJitHeapBytes: totalJitHeapBytes,
            JitManagerCount: 3,
            ActiveMethodsOnStacks: managedFrames,
            TopLargestMethods: topMethods,
            TopActiveFrameTypes: [],
            UnmanagedFrameCount: unmanagedFrames,
            ManagedFrameCount: managedFrames,
            TieredMethodCount: tieredMethods);
    }
}
