using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class MemoryFindingGeneratorTests
{
    private readonly MemoryFindingGenerator _gen = new();

    [Fact]
    public void Generate_Top1BytesPercentAboveThreshold_EmitsDominantTypeFinding()
    {
        MemoryDomainResult result = BuildResult(top1BytesPercent: 55.0, top5BytesPercent: 60.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("dominant-type")
            && f.Evidence.Contains("MyApp.Cache.Entry")
            && f.Evidence.Contains("55.0%"));
    }

    [Fact]
    public void Generate_Top1BytesPercentAtThreshold_EmitsNoDominantTypeFinding()
    {
        MemoryDomainResult result = BuildResult(top1BytesPercent: 40.0, top5BytesPercent: 50.0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("dominant-type"));
    }

    [Fact]
    public void Generate_MemoryPressureScoreAtOrAboveSeventy_EmitsWarningFinding()
    {
        MemoryDomainResult result = BuildResult(memoryPressureScore: 85.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("pressure")
            && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_MemoryPressureScoreBelowSeventy_EmitsNoPressureFinding()
    {
        MemoryDomainResult result = BuildResult(memoryPressureScore: 69.9);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "High memory pressure score");
    }

    [Fact]
    public void Generate_Top5BytesPercentAboveEighty_EmitsWarningFinding()
    {
        MemoryDomainResult result = BuildResult(top1BytesPercent: 0, top5BytesPercent: 85.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Title == "High memory concentration in few types"
            && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_SmallObjectCountPercentAboveEightyFive_EmitsInfoFinding()
    {
        MemoryDomainResult result = BuildResult(smallObjectCountPercent: 90.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("allocation")
            && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_LohPercentAtOrAboveForty_EmitsWarningFinding()
    {
        MemoryDomainResult result = BuildResult(lohPercent: 45.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("loh")
            && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoThresholdsCrossed_EmitsBaselineInfoSnapshot()
    {
        MemoryDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Should().Match<InsightFinding>(f =>
            f.Severity == FindingSeverity.Info && f.Title == "Managed heap memory snapshot");
    }

    private static MemoryDomainResult BuildResult(
        double top1BytesPercent = 0,
        double top5BytesPercent = 0,
        double memoryPressureScore = 0,
        double smallObjectCountPercent = 0,
        double lohPercent = 0)
    {
        return new MemoryDomainResult(
            TotalBytes: 1_000_000_000,
            LohBytes: (ulong)(1_000_000_000 * (lohPercent / 100.0)),
            LohPercent: lohPercent,
            TotalObjects: 100_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: [new TypeSnapshot("MyApp.Cache.Entry", 10_000, (ulong)(1_000_000_000 * (top1BytesPercent / 100.0)), 0)],
            Top1BytesPercent: top1BytesPercent,
            Top5BytesPercent: top5BytesPercent,
            SmallObjectCountPercent: smallObjectCountPercent,
            MemoryPressureScore: memoryPressureScore);
    }
}
