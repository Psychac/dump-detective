using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class GCRootFindingGeneratorTests
{
    private readonly GCRootFindingGenerator _gen = new();

    [Fact]
    public void Generate_StaticRootBytesAtOrAboveOneHundredMb_EmitsCriticalFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "StrongHandle", Count: 10, EstimatedRetainedBytes: 150_000_000UL, PctOfManagedHeap: 15.0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("static") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_StaticRootBytesAtOrAboveTenMb_BelowCritical_EmitsWarningFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "StrongHandle", Count: 5, EstimatedRetainedBytes: 20_000_000UL, PctOfManagedHeap: 2.0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("static") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_StaticRootBytesBelowTenMb_EmitsNoStaticRootFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "StrongHandle", Count: 1, EstimatedRetainedBytes: 1_000_000UL, PctOfManagedHeap: 0.1)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("static"));
    }

    [Fact]
    public void Generate_FinalizerCountAtOrAboveFiveThousand_EmitsWarningFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "FinalizerQueue", Count: 6_000, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("finalizer") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_FinalizerCountAtOrAboveFiveHundred_BelowFiveThousand_EmitsInfoFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "FinalizerQueue", Count: 800, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("finalizer") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_PinnedHandlesAtOrAboveFiveHundred_EmitsWarningFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "PinnedHandle", Count: 600, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("pinned") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_PinnedHandlesAtOrAboveOneHundred_BelowFiveHundred_EmitsInfoFinding()
    {
        GCRootDomainResult result = BuildResult(byKind:
        [
            new RootKindSummary(Kind: "AsyncPinnedHandle", Count: 150, EstimatedRetainedBytes: 0, PctOfManagedHeap: 0)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("pinned") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_SubgraphWalkCapped_EmitsInfoFinding()
    {
        GCRootDomainResult result = BuildResult(subgraphWalkCapped: true, subgraphWalkCappedCount: 3);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("cap") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_NoSignals_EmitsNoFindings()
    {
        GCRootDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().BeEmpty();
    }

    private static GCRootDomainResult BuildResult(
        IReadOnlyList<RootKindSummary>? byKind = null,
        bool subgraphWalkCapped = false,
        int subgraphWalkCappedCount = 0) => new(
        TotalRoots: 100,
        ByKind: byKind ?? [],
        TopRootsBySeverity: [],
        RootOwnedSubgraphs: [],
        SubgraphWalkCapped: subgraphWalkCapped,
        SubgraphWalkCappedCount: subgraphWalkCappedCount);
}
