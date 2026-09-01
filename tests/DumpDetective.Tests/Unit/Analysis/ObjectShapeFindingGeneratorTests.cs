using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ObjectShapeFindingGeneratorTests
{
    private static TypeShapeProfile RefHeavyType(string name = "Demo.Widget") =>
        new(name, TotalFields: 10, ReferenceFields: 9, ValueFields: 1, ReferenceFieldRatio: 0.9,
            InstanceCount: 1_000, TotalSize: 100_000, IsFinalizable: false, IsValueType: false, IsArray: false,
            BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ReferenceHeavy);

    private static ObjectShapeAnalyzerDomainResult BuildResult(double avgRefFieldsPerType, long totalGcScanWork) =>
        new(
            TopReferenceHeavyTypes: [RefHeavyType()],
            TopValueHeavyTypes: [],
            TopBalancedTypes: [],
            TotalTypesAnalyzed: 5,
            AvgRefFieldsPerType: avgRefFieldsPerType,
            TotalGcScanWork: totalGcScanWork,
            TopGen2RetainedTypes: [],
            TotalGen2GcScanWork: 0);

    [Fact]
    public void Generate_LowAvgRefFields_NoDensityFinding()
    {
        var result = BuildResult(avgRefFieldsPerType: 2.0, totalGcScanWork: 1_000);

        var findings = new ObjectShapeFindingGenerator().Generate(result);

        findings.Should().NotContain(f => f.Title.StartsWith("High reference-field density"));
    }

    [Fact]
    public void Generate_ModerateAvgRefFields_DensityFindingIsInfo()
    {
        var result = BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 1_000);

        InsightFinding finding = new ObjectShapeFindingGenerator().Generate(result)
            .Single(f => f.Title.StartsWith("High reference-field density"));

        finding.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_HighAvgRefFields_DensityFindingIsWarning()
    {
        var result = BuildResult(avgRefFieldsPerType: 9.0, totalGcScanWork: 1_000);

        InsightFinding finding = new ObjectShapeFindingGenerator().Generate(result)
            .Single(f => f.Title.StartsWith("High reference-field density"));

        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_TotalGcScanWorkExceedsCriticalThreshold_DensityFindingIsCritical()
    {
        var result = BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 300_000_000L);

        InsightFinding finding = new ObjectShapeFindingGenerator().Generate(result)
            .Single(f => f.Title.StartsWith("High reference-field density"));

        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.Evidence.Should().Contain(300_000_000L.ToString("N0"));
    }

    [Fact]
    public void Generate_TotalGcScanWorkBelowCriticalThreshold_HighAvgRefFieldsStaysWarning()
    {
        var result = BuildResult(avgRefFieldsPerType: 9.0, totalGcScanWork: 100_000_000L);

        InsightFinding finding = new ObjectShapeFindingGenerator().Generate(result)
            .Single(f => f.Title.StartsWith("High reference-field density"));

        finding.Severity.Should().Be(FindingSeverity.Warning);
    }
}
