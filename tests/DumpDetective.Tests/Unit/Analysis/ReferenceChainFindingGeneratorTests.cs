using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ReferenceChainFindingGeneratorTests
{
    [Fact]
    public void Generate_NoAnalyzedSamples_EmitsOnlyNoSampleFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var result = BuildResult(analyzedSamples: 0, retainedSamples: 0, retainedPercent: 0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle()
            .Which.Title.Should().Be("No sample instances available for reference-chain tracing");
    }

    [Fact]
    public void Generate_RetentionBelowThreshold_EmitsInfoRetentionCoverage()
    {
        var gen = new ReferenceChainFindingGenerator();
        var result = BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title == "Reference-chain retention coverage")
            .Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_RetentionAboveThreshold_EmitsWarningRetentionCoverage()
    {
        var gen = new ReferenceChainFindingGenerator();
        var result = BuildResult(analyzedSamples: 10, retainedSamples: 8, retainedPercent: 80.0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title == "Reference-chain retention coverage")
            .Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoTraversalLimitedSamples_EmitsNoTraversalLimitFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var result = BuildResult(analyzedSamples: 10, retainedSamples: 5, retainedPercent: 50.0, traversalLimitedSamples: 0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "Reference-chain traversal limit reached");
    }

    [Fact]
    public void Generate_NoTypesExclusivelyFinalizerRetained_EmitsNoFinalizerOnlyFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var traces = new[]
        {
            BuildTrace("App.Widget", count: 500, retainedSampleCount: 3, sampleCount: 3,
                dominantSampleRootKind: "StaticVar", dominantSampleRootKindCount: 3),
        };
        var result = BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0, traces: traces);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "Types exclusively retained via finalizer queue");
    }

    [Fact]
    public void Generate_TypePartiallyFinalizerRetained_EmitsNoFinalizerOnlyFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var traces = new[]
        {
            BuildTrace("App.Handle", count: 500, retainedSampleCount: 5, sampleCount: 5,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 4),
        };
        var result = BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0, traces: traces);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "Types exclusively retained via finalizer queue");
    }

    [Fact]
    public void Generate_TypeExclusivelyFinalizerRetainedBelowPopulationThreshold_EmitsInfoFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var traces = new[]
        {
            BuildTrace("App.SmallHandle", count: 40, retainedSampleCount: 3, sampleCount: 3,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 3),
        };
        var result = BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0, traces: traces);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Types exclusively retained via finalizer queue").Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
        finding.Evidence.Should().Contain("App.SmallHandle");
    }

    [Fact]
    public void Generate_TypeExclusivelyFinalizerRetainedAtScale_EmitsWarningFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var traces = new[]
        {
            BuildTrace("App.LeakyHandle", count: 5_000, retainedSampleCount: 3, sampleCount: 3,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 3),
        };
        var result = BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0, traces: traces);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Types exclusively retained via finalizer queue").Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("App.LeakyHandle");
        finding.MetricValue.Should().Be(1);
    }

    [Fact]
    public void Generate_MultipleTypesExclusivelyFinalizerRetained_ListsAllInEvidence()
    {
        var gen = new ReferenceChainFindingGenerator();
        var traces = new[]
        {
            BuildTrace("App.HandleA", count: 40, retainedSampleCount: 2, sampleCount: 2,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 2),
            BuildTrace("App.HandleB", count: 5_000, retainedSampleCount: 4, sampleCount: 4,
                dominantSampleRootKind: "Finalizer", dominantSampleRootKindCount: 4),
        };
        var result = BuildResult(analyzedSamples: 2, retainedSamples: 2, retainedPercent: 100.0, traces: traces);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Types exclusively retained via finalizer queue").Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("App.HandleA").And.Contain("App.HandleB");
        finding.MetricValue.Should().Be(2);
    }

    [Fact]
    public void Generate_NoSharedRootGroups_EmitsNoSharedRootFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var result = BuildResult(analyzedSamples: 1, retainedSamples: 1, retainedPercent: 100.0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "Shared root retention hubs across top types");
    }

    [Fact]
    public void Generate_SharedRootGroupBelowTypeCountThreshold_EmitsInfoFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var sharedRootGroups = new[]
        {
            new ReferenceChainSharedRootGroup(0x2000, "StaticVar", ["App.CacheA", "App.CacheB"]),
        };
        var result = BuildResult(analyzedSamples: 2, retainedSamples: 2, retainedPercent: 100.0, sharedRootGroups: sharedRootGroups);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Shared root retention hubs across top types").Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
        finding.Evidence.Should().Contain("App.CacheA").And.Contain("App.CacheB");
    }

    [Fact]
    public void Generate_SharedRootGroupAtTypeCountThreshold_EmitsWarningFinding()
    {
        var gen = new ReferenceChainFindingGenerator();
        var sharedRootGroups = new[]
        {
            new ReferenceChainSharedRootGroup(0x3000, "StaticVar", ["App.CacheA", "App.CacheB", "App.CacheC"]),
        };
        var result = BuildResult(analyzedSamples: 3, retainedSamples: 3, retainedPercent: 100.0, sharedRootGroups: sharedRootGroups);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Shared root retention hubs across top types").Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.MetricValue.Should().Be(1);
    }

    [Fact]
    public void Generate_MultipleSharedRootGroups_ListsAllInEvidence()
    {
        var gen = new ReferenceChainFindingGenerator();
        var sharedRootGroups = new[]
        {
            new ReferenceChainSharedRootGroup(0x3000, "StaticVar", ["App.CacheA", "App.CacheB", "App.CacheC"]),
            new ReferenceChainSharedRootGroup(0x4000, "Stack", ["App.HandleD", "App.HandleE"]),
        };
        var result = BuildResult(analyzedSamples: 5, retainedSamples: 5, retainedPercent: 100.0, sharedRootGroups: sharedRootGroups);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "Shared root retention hubs across top types").Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("0x3000").And.Contain("0x4000");
        finding.MetricValue.Should().Be(2);
    }

    private static ReferenceTypeSampleSnapshot BuildTrace(
        string typeName,
        int count,
        int retainedSampleCount,
        int sampleCount,
        string? dominantSampleRootKind,
        int dominantSampleRootKindCount)
    {
        return new ReferenceTypeSampleSnapshot(
            TypeName: typeName,
            Count: count,
            TotalSizeBytes: (ulong)count * 32,
            SampleAddress: 0x1000,
            SampleObjectType: typeName,
            SampleObjectSize: 32,
            HasGcRoot: retainedSampleCount > 0,
            RootKind: dominantSampleRootKind,
            RootPath: null,
            PathHops: null,
            TraversalLimited: false,
            SampleCount: sampleCount,
            RetainedSampleCount: retainedSampleCount,
            DominantSampleRootKind: dominantSampleRootKind,
            DominantSampleRootKindCount: dominantSampleRootKindCount);
    }

    private static ReferenceChainDomainResult BuildResult(
        int analyzedSamples,
        int retainedSamples,
        double retainedPercent,
        int traversalLimitedSamples = 0,
        IReadOnlyList<ReferenceTypeSampleSnapshot>? traces = null,
        IReadOnlyList<ReferenceChainSharedRootGroup>? sharedRootGroups = null)
    {
        return new ReferenceChainDomainResult(
            AnalyzedSamples: analyzedSamples,
            RetainedSamples: retainedSamples,
            RetainedPercent: retainedPercent,
            TopTypeSampleTraces: traces,
            TraversalLimitedSamples: traversalLimitedSamples,
            SharedRootGroups: sharedRootGroups);
    }
}
