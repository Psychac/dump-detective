using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class GCGenerationFindingGeneratorTests
{
    private readonly GCGenerationFindingGenerator _gen = new();

    [Fact]
    public void Generate_Gen2PctAboveSeventyFive_EmitsCriticalFinding()
    {
        GCGenerationDomainResult result = BuildResult(gen2Pct: 80.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("gen2") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_Gen2PctBetweenFiftyAndSeventyFive_EmitsWarningFinding()
    {
        GCGenerationDomainResult result = BuildResult(gen2Pct: 60.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("gen2") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_Gen2PctBelowFifty_EmitsNoGen2Finding()
    {
        GCGenerationDomainResult result = BuildResult(gen2Pct: 40.0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("gen2"));
    }

    [Fact]
    public void Generate_Gen0PctAtOrAboveThreshold_EmitsWarningFinding()
    {
        GCGenerationDomainResult result = BuildResult(totalObjects: 1000, gen0Objects: 500, gen0PressureThresholdPercent: 40.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("gen0") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LohPctAtThreshold_BelowThirtyFive_EmitsInfoFinding()
    {
        GCGenerationDomainResult result = BuildResult(lohPercent: 25.0, lohThresholdPercent: 20.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("loh") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_LohPctAtOrAboveThirtyFive_EmitsWarningFinding()
    {
        GCGenerationDomainResult result = BuildResult(lohPercent: 40.0, lohThresholdPercent: 20.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("loh") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_PohPctAtThreshold_BelowTwenty_EmitsInfoFinding()
    {
        GCGenerationDomainResult result = BuildResult(totalObjects: 1000, pohObjects: 100, pohBytes: 1024, pohThresholdPercent: 5.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("poh") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_PohPctAtOrAboveTwenty_EmitsWarningFinding()
    {
        GCGenerationDomainResult result = BuildResult(totalObjects: 1000, pohObjects: 250, pohBytes: 1024, pohThresholdPercent: 5.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("poh") && f.Severity == FindingSeverity.Warning);
    }

    private static GCGenerationDomainResult BuildResult(
        int totalObjects = 10_000,
        long gen0Objects = 0,
        long pohObjects = 0,
        ulong pohBytes = 0,
        double gen2Pct = 0.0,
        double lohPercent = 0.0,
        double lohThresholdPercent = 20.0,
        double gen0PressureThresholdPercent = 40.0,
        double pohThresholdPercent = 5.0) => new(
        Gen0Bytes: 0, Gen0Objects: gen0Objects,
        Gen1Bytes: 0, Gen1Objects: 0,
        Gen2Bytes: 0, Gen2Objects: 0,
        LohBytes: 0, LohPercent: lohPercent,
        TotalObjects: totalObjects, LohObjects: 0,
        TopLohTypes: [],
        PohBytes: pohBytes, PohObjects: pohObjects,
        Gen2Pct: gen2Pct,
        LohThresholdPercent: lohThresholdPercent,
        Gen0PressureThresholdPercent: gen0PressureThresholdPercent,
        PohThresholdPercent: pohThresholdPercent);

    private static GCGenerationDomainResult BuildGen2PressureResult(long finalizableGen2Count, ulong finalizableGen2Bytes) =>
        new(
            Gen0Bytes: 10_000, Gen0Objects: 10,
            Gen1Bytes: 10_000, Gen1Objects: 10,
            Gen2Bytes: 800_000, Gen2Objects: 800,
            LohBytes: 0, LohPercent: 0,
            TotalObjects: 820, LohObjects: 0,
            TopLohTypes: [],
            Gen2Pct: 97.6,
            FinalizableGen2Count: finalizableGen2Count,
            FinalizableGen2Bytes: finalizableGen2Bytes);

    [Fact]
    public void Generate_Gen2Finding_IncludesFinalizableCrossReference_WhenFinalizableGen2ObjectsPresent()
    {
        GCGenerationDomainResult result = BuildGen2PressureResult(finalizableGen2Count: 150, finalizableGen2Bytes: 45_000);

        InsightFinding finding = new GCGenerationFindingGenerator().Generate(result)
            .Single(f => f.Title.Contains("Gen2 holds"));

        finding.Evidence.Should().Contain("150");
        finding.Evidence.Should().Contain("Finalizable Object Analysis");
    }

    [Fact]
    public void Generate_Gen2Finding_OmitsFinalizableCrossReference_WhenNoFinalizableGen2Objects()
    {
        GCGenerationDomainResult result = BuildGen2PressureResult(finalizableGen2Count: 0, finalizableGen2Bytes: 0);

        InsightFinding finding = new GCGenerationFindingGenerator().Generate(result)
            .Single(f => f.Title.Contains("Gen2 holds"));

        finding.Evidence.Should().NotContain("Finalizable Object Analysis");
    }
}
