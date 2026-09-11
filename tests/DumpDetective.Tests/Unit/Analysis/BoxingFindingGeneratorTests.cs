using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class BoxingFindingGeneratorTests
{
    private readonly BoxingFindingGenerator _gen = new();

    [Fact]
    public void Generate_BoxedEnumCountAboveOneMillion_EmitsCriticalFinding()
    {
        BoxingDomainResult result = BuildResult(boxedEnumCount: 2_000_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("enum") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_BoxedEnumCountAboveFiftyThousand_EmitsWarningFinding()
    {
        BoxingDomainResult result = BuildResult(boxedEnumCount: 60_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("enum") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_BoxedEnumCountAboveOneThousand_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(boxedEnumCount: 5_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("enum") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_NullableBoxedCountAboveTenThousand_EmitsWarningFinding()
    {
        BoxingDomainResult result = BuildResult(nullableBoxedCount: 15_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("nullable") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NullableBoxedCountAboveOneHundred_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(nullableBoxedCount: 500);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("nullable") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_StructPaddingWasteRatioAboveQuarter_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(topPaddingWasteTypes:
        [
            new StructPaddingEntry(TypeName: "MyApp.Point3D", TotalFieldBytes: 12, StructSize: 24, WastedPaddingBytes: 12, WasteRatio: 0.5)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("padding") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_OversizedValueTypeCountAboveFiveHundredThousand_EmitsCriticalFinding()
    {
        BoxingDomainResult result = BuildResult(oversizedValueTypeInstanceCount: 600_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("value-type") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_OversizedValueTypeCountAboveOneHundredThousand_EmitsWarningFinding()
    {
        BoxingDomainResult result = BuildResult(oversizedValueTypeInstanceCount: 150_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("value-type") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_OversizedValueTypeCountAboveOneHundred_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(oversizedValueTypeInstanceCount: 500);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("value-type") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_Gen2FractionAboveEightyPercent_EmitsWarningFinding()
    {
        BoxingDomainResult result = BuildResult(totalBoxedObjects: 2000, totalGen2BoxedCount: 1900);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("retained") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_Gen2FractionBetweenFiftyAndEightyPercent_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(totalBoxedObjects: 2000, totalGen2BoxedCount: 1200);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("retained") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_Gen2FractionAtOrBelowFiftyPercent_EmitsNoRetainedFinding()
    {
        BoxingDomainResult result = BuildResult(totalBoxedObjects: 2000, totalGen2BoxedCount: 900);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("retained"));
    }

    [Fact]
    public void Generate_MissingEquatableInstancesAboveOneThousand_EmitsInfoFinding()
    {
        BoxingDomainResult result = BuildResult(topBoxedTypes:
        [
            new BoxedTypeEntry("MyApp.Money", 2_000, 32_000, IsEnum: false, HasReferenceFields: false, Gen0Count: 1000, Gen2Count: 100, Gen2Fraction: 0.05, HasIEquatable: false)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("equality") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_AllBoxedTypesHaveIEquatable_EmitsNoEqualityFinding()
    {
        BoxingDomainResult result = BuildResult(topBoxedTypes:
        [
            new BoxedTypeEntry("MyApp.Money", 2_000, 32_000, IsEnum: false, HasReferenceFields: false, Gen0Count: 1000, Gen2Count: 100, Gen2Fraction: 0.05, HasIEquatable: true)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("equality"));
    }

    [Fact]
    public void Generate_TotalBoxedObjectsAboveOneMillion_SummaryIsCritical()
    {
        BoxingDomainResult result = BuildResult(totalBoxedObjects: 1_500_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Boxing pressure overview" && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_TotalBoxedObjectsAboveFiveHundredThousand_SummaryIsWarning()
    {
        BoxingDomainResult result = BuildResult(totalBoxedObjects: 600_000);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Boxing pressure overview" && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_AllBelowThresholds_EmitsOnlyInfoSummaryFinding()
    {
        BoxingDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Should().Match<InsightFinding>(f =>
            f.Title == "Boxing pressure overview" && f.Severity == FindingSeverity.Info);
    }

    private static BoxingDomainResult BuildResult(
        long boxedEnumCount = 0,
        long nullableBoxedCount = 0,
        IReadOnlyList<StructPaddingEntry>? topPaddingWasteTypes = null,
        long oversizedValueTypeInstanceCount = 0,
        long totalBoxedObjects = 500,
        long totalGen2BoxedCount = 0,
        IReadOnlyList<BoxedTypeEntry>? topBoxedTypes = null) => new(
        TotalBoxedObjects: totalBoxedObjects,
        TotalBoxedBytes: (ulong)totalBoxedObjects * 16,
        TopBoxedTypes: topBoxedTypes ?? [],
        BoxedEnumCount: boxedEnumCount,
        BoxedEnumBytes: (ulong)boxedEnumCount * 16,
        NullableBoxedCount: nullableBoxedCount,
        NullableBoxedBytes: (ulong)nullableBoxedCount * 16,
        OversizedValueTypeInstanceCount: oversizedValueTypeInstanceCount,
        TopOversizedTypes: [],
        TopPaddingWasteTypes: topPaddingWasteTypes ?? [],
        AggregatePaddingWasteBytes: 0,
        AvgBoxedInstanceBytes: 16.0,
        TotalGen2BoxedCount: totalGen2BoxedCount);
}
