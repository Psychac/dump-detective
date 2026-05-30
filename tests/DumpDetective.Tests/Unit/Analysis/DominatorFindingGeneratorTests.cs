using DumpDetective.Analysis.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class DominatorFindingGeneratorTests
{
    [Fact]
    public void Generate_ShouldReturnEmpty_WhenNoTopTypes()
    {
        DominatorFindingGenerator generator = new();
        DominatorDomainResult result = new(0, 0, 0, []);

        generator.CanGenerate(result).Should().BeTrue();
        generator.Generate(result).Should().BeEmpty();
    }

    [Theory]
    [InlineData(80UL * 1024 * 1024, FindingSeverity.Info)]
    [InlineData(100UL * 1024 * 1024, FindingSeverity.Warning)]
    [InlineData(700UL * 1024 * 1024, FindingSeverity.Critical)]
    public void Generate_ShouldMapSeverity_FromEstimatedRetainedBytes(ulong retainedBytes, FindingSeverity expected)
    {
        DominatorFindingGenerator generator = new();
        DominatorDomainResult result = BuildResult(retainedBytes);

        IReadOnlyList<InsightFinding> findings = generator.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(expected);
        findings[0].Analyzer.Should().Be("Dominator Analysis");
        findings[0].MetricValue.Should().Be(retainedBytes);
        findings[0].MetricUnit.Should().Be("bytes");
    }

    private static DominatorDomainResult BuildResult(ulong retainedBytes)
    {
        TypeSnapshot top = new(
            TypeName: "MyApp.BigGraph",
            Count: 12,
            TotalBytes: 24_000_000,
            LohBytes: 0,
            AverageSize: 2_000_000,
            EstimatedRetainedBytes: retainedBytes,
            SampleAddress: 0x1234UL,
            ModuleName: "MyApp.Core");

        return new DominatorDomainResult(
            CandidateCount: 4,
            AnalyzedCount: 4,
            TotalEstimatedRetainedBytes: retainedBytes,
            TopDominatorTypes: [top]);
    }
}
