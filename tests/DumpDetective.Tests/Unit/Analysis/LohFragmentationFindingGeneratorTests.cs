using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class LohFragmentationFindingGeneratorTests
{
    private readonly LohFragmentationFindingGenerator _gen = new();

    [Fact]
    public void Generate_FragmentationAtOrAboveThirty_EmitsCriticalFinding()
    {
        LohFragmentationDomainResult result = BuildResult(fragmentationPercent: 35.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_FragmentationAtThirtyBoundary_EmitsCriticalFinding()
    {
        LohFragmentationDomainResult result = BuildResult(fragmentationPercent: 30.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_FragmentationBetweenFifteenAndThirty_EmitsWarningFinding()
    {
        LohFragmentationDomainResult result = BuildResult(fragmentationPercent: 20.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_FragmentationAtFifteenBoundary_EmitsWarningFinding()
    {
        LohFragmentationDomainResult result = BuildResult(fragmentationPercent: 15.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_FragmentationBelowFifteen_EmitsInfoFinding()
    {
        LohFragmentationDomainResult result = BuildResult(fragmentationPercent: 5.0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    private static LohFragmentationDomainResult BuildResult(double fragmentationPercent) => new(
        SegmentCount: 4,
        TotalBytes: 400_000_000,
        FreeBytes: (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        UsedBytes: 400_000_000 - (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        FreeBlockCount: 10,
        FragmentationPercent: fragmentationPercent,
        LargestFreeBlock: 1_000_000);
}
