using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class GCGenerationFindingGeneratorTests
{
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
