using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class MemoryFindingGeneratorTests
{
    [Fact]
    public void Generate_Top1BytesPercentAboveThreshold_EmitsDominantTypeFinding()
    {
        var gen = new MemoryFindingGenerator();
        MemoryDomainResult result = BuildResult(top1BytesPercent: 55.0, top5BytesPercent: 60.0);

        var findings = gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("dominant-type")
            && f.Evidence.Contains("MyApp.Cache.Entry")
            && f.Evidence.Contains("55.0%"));
    }

    [Fact]
    public void Generate_Top1BytesPercentAtThreshold_EmitsNoDominantTypeFinding()
    {
        var gen = new MemoryFindingGenerator();
        MemoryDomainResult result = BuildResult(top1BytesPercent: 40.0, top5BytesPercent: 50.0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("dominant-type"));
    }

    private static MemoryDomainResult BuildResult(double top1BytesPercent, double top5BytesPercent)
    {
        return new MemoryDomainResult(
            TotalBytes: 1_000_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 100_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: [new TypeSnapshot("MyApp.Cache.Entry", 10_000, (ulong)(1_000_000_000 * (top1BytesPercent / 100.0)), 0)],
            Top1BytesPercent: top1BytesPercent,
            Top5BytesPercent: top5BytesPercent);
    }
}
