using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class HeapTopologyFindingGeneratorTests
{
    private readonly HeapTopologyFindingGenerator _gen = new();

    [Fact]
    public void Generate_LohPercentAtOrAboveForty_OverviewIsCritical()
    {
        HeapTopologyDomainResult result = BuildResult(lohPercent: 50.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Heap segment distribution snapshot" && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_LohPercentBetweenTwentyFiveAndForty_OverviewIsWarning()
    {
        HeapTopologyDomainResult result = BuildResult(lohPercent: 30.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Heap segment distribution snapshot" && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LohPercentBelowTwentyFive_OverviewIsInfo()
    {
        HeapTopologyDomainResult result = BuildResult(lohPercent: 10.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Heap segment distribution snapshot" && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_PohPercentAboveTen_EmitsWarningFinding()
    {
        HeapTopologyDomainResult result = BuildResult(pohPercent: 15.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("poh") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LogicalHeapsSkewedAboveTwoX_EmitsWarningFinding()
    {
        HeapTopologyDomainResult result = BuildResult(perLogicalHeapSummaries:
        [
            new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 100_000_000, ObjectCount: 1000, SegmentCount: 2),
            new PerLogicalHeapSummary(LogicalHeapIndex: 1, Bytes: 300_000_000, ObjectCount: 3000, SegmentCount: 2),
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("skew") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_UnrecognizedSegmentKind_EmitsWarningFinding()
    {
        HeapTopologyDomainResult result = BuildResult(kindSummaries:
        [
            new SegmentKindSummary(Kind: HeapSegmentKind.Unknown, SegmentCount: 2, ObjectCount: 100, TotalBytes: 1_000_000, ReservedBytes: 2_000_000)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Severity == FindingSeverity.Warning && f.Tags.Contains("memory") && f.Evidence.Contains("2"));
    }

    [Fact]
    public void Generate_SohFragmentationAtOrAboveFortyFive_EmitsWarningFinding()
    {
        HeapTopologyDomainResult result = BuildResult(sohBytes: 1000, sohFragmentedBytes: 460);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("soh") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_SohFragmentationBetweenTwentyFiveAndFortyFive_EmitsInfoFinding()
    {
        HeapTopologyDomainResult result = BuildResult(sohBytes: 1000, sohFragmentedBytes: 300);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("soh") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_LohFragmentationAtOrAboveFifty_EmitsCriticalFinding()
    {
        HeapTopologyDomainResult result = BuildResult(lohBytes: 1000, lohFragmentedBytes: 550);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("LOH fragmentation") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_LohFragmentationBetweenThirtyAndFifty_EmitsWarningFinding()
    {
        HeapTopologyDomainResult result = BuildResult(lohBytes: 1000, lohFragmentedBytes: 350);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("LOH fragmentation") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_PohFragmentationAtOrAboveTwenty_EmitsInfoFinding()
    {
        HeapTopologyDomainResult result = BuildResult(pohBytes: 1000, pohFragmentedBytes: 250);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("poh") && f.Tags.Contains("fragmentation") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_FrozenFragmentationAtOrAboveThirty_EmitsInfoFinding()
    {
        HeapTopologyDomainResult result = BuildResult(frozenBytes: 1000, frozenFragmentedBytes: 350);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("frozen") && f.Severity == FindingSeverity.Info);
    }

    private static HeapTopologyDomainResult BuildResult(
        double lohPercent = 10.0,
        double pohPercent = 0.0,
        IReadOnlyList<PerLogicalHeapSummary>? perLogicalHeapSummaries = null,
        IReadOnlyList<SegmentKindSummary>? kindSummaries = null,
        ulong sohBytes = 0,
        ulong sohFragmentedBytes = 0,
        ulong lohBytes = 0,
        ulong lohFragmentedBytes = 0,
        ulong pohBytes = 0,
        ulong pohFragmentedBytes = 0,
        ulong frozenBytes = 0,
        ulong frozenFragmentedBytes = 0) => new(
        TotalSegments: 10,
        TotalCommittedBytes: 1_000_000_000,
        TotalUsedBytes: 800_000_000,
        TotalReservedBytes: 1_500_000_000,
        ReservationGapBytes: 500_000_000,
        SohSegmentCount: 6,
        SohBytes: sohBytes,
        LohSegmentCount: 2,
        LohBytes: lohBytes,
        PohSegmentCount: 1,
        PohBytes: pohBytes,
        FrozenSegmentCount: 1,
        FrozenBytes: frozenBytes,
        FrozenPercent: 0,
        LohPercent: lohPercent,
        PohPercent: pohPercent,
        Gen0Bytes: 0,
        Gen1Bytes: 0,
        Gen2Bytes: 0,
        SohFragmentedBytes: sohFragmentedBytes,
        LohFragmentedBytes: lohFragmentedBytes,
        PohFragmentedBytes: pohFragmentedBytes,
        FrozenFragmentedBytes: frozenFragmentedBytes,
        IsServerGc: false,
        LogicalHeapCount: 1,
        KindSummaries: kindSummaries ?? [],
        PerLogicalHeapSummaries: perLogicalHeapSummaries ?? []);
}
