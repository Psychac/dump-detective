using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class StaticRootFindingGeneratorTests
{
    private readonly StaticRootFindingGenerator _gen = new();

    [Fact]
    public void Generate_NoRoots_EmitsInfoFinding()
    {
        StaticRootDomainResult result = BuildResult(rootCount: 0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_RootCountAtOrAboveTen_EmitsCriticalFinding()
    {
        StaticRootDomainResult result = BuildResult(rootCount: 15);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_TopRootExceedsCriticalByteThreshold_EmitsCriticalFinding()
    {
        StaticRootDomainResult result = BuildResult(
            rootCount: 3,
            topRoots: [new StaticRootSnapshot(RootDescription: "MyApp.Cache.Instance", TotalMemoryImpact: 200 * 1024 * 1024UL, ObjectsKeptAlive: 5000)]);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_BelowRootCountAndByteThresholds_EmitsWarningFinding()
    {
        StaticRootDomainResult result = BuildResult(
            rootCount: 3,
            topRoots: [new StaticRootSnapshot(RootDescription: "MyApp.Cache.Instance", TotalMemoryImpact: 1024, ObjectsKeptAlive: 10)]);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    private static StaticRootDomainResult BuildResult(int rootCount, IReadOnlyList<StaticRootSnapshot>? topRoots = null) => new(
        RootCount: rootCount,
        TotalRetainedBytes: 1024,
        TopRootsByRetainedBytes: topRoots ?? []);
}
