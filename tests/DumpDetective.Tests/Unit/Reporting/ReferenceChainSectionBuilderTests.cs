using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ReferenceChainSectionBuilderTests
{
    private readonly ReferenceChainSectionBuilder _builder = new();

    private static ReferenceChainDomainResult Result(
        int retainedSamples,
        IReadOnlyList<ReferenceChainRootKindCount>? rootKindDistribution,
        int noSampleAddressCount = 0,
        IReadOnlyList<ReferenceTypeSampleSnapshot>? topTypeSampleTraces = null) =>
        new(
            AnalyzedSamples: 5,
            RetainedSamples: retainedSamples,
            RetainedPercent: 100.0,
            RootKindDistribution: rootKindDistribution,
            NoSampleAddressCount: noSampleAddressCount,
            TopTypeSampleTraces: topTypeSampleTraces);

    [Fact]
    public void Build_WithRootKindDistribution_AddsRootKindDistributionTable()
    {
        var result = Result(
            retainedSamples: 5,
            rootKindDistribution:
            [
                new ReferenceChainRootKindCount("StaticVar", 3),
                new ReferenceChainRootKindCount("Stack", 2),
            ]);

        var section = _builder.Build(result);

        section.CompactTables.Should().NotBeNull();
        var table = section.CompactTables!.Should().ContainSingle(t => t.Title == "Root kind distribution").Subject;
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Values[0].Should().Be("StaticVar");
        table.Rows[0].Values[1].Should().Be(3);
        table.Rows[0].Values[2].Should().Be(60.0);
        table.Rows[1].Values[0].Should().Be("Stack");
        table.Rows[1].Values[1].Should().Be(2);
        table.Rows[1].Values[2].Should().Be(40.0);
    }

    [Fact]
    public void Build_NoRootKindDistribution_OmitsRootKindDistributionTable()
    {
        var result = Result(retainedSamples: 0, rootKindDistribution: null);

        var section = _builder.Build(result);

        (section.CompactTables ?? []).Should().NotContain(t => t.Title == "Root kind distribution");
    }

    [Fact]
    public void Build_TypesWithNoSampleAddress_AddsNoSampleAddressCountKeyMetric()
    {
        var result = Result(retainedSamples: 0, rootKindDistribution: null, noSampleAddressCount: 3);

        var section = _builder.Build(result);

        section.KeyMetrics.Should().ContainKey("no_sample_address_count");
        section.KeyMetrics!["no_sample_address_count"].Should().BeOfType<NumericMetricValue>()
            .Which.Value.Should().Be(3d);
    }

    private static ReferenceTypeSampleSnapshot MultiSampleSnapshot(
        bool hasGcRoot, int sampleCount, int retainedSampleCount, string? dominantRootKind, int dominantRootKindCount,
        ulong? retainedBytes = null, string? rootFieldName = null, string? lastHopFieldName = null) =>
        new(
            TypeName: "MyApp.Widget",
            Count: 1000,
            TotalSizeBytes: 24_000,
            SampleAddress: 0x1000,
            SampleObjectType: "MyApp.Widget",
            SampleObjectSize: 24,
            HasGcRoot: hasGcRoot,
            RootKind: dominantRootKind,
            RootPath: null,
            PathHops: null,
            TraversalLimited: false,
            SampleCount: sampleCount,
            RetainedSampleCount: retainedSampleCount,
            DominantSampleRootKind: dominantRootKind,
            DominantSampleRootKindCount: dominantRootKindCount,
            RetainedBytes: retainedBytes,
            RootFieldName: rootFieldName,
            LastHopFieldName: lastHopFieldName);

    [Fact]
    public void Build_MultiSampleWithDominantRootKind_AppendsConsistencyReadToStatusLabel()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 5, retainedSampleCount: 4,
            dominantRootKind: "StaticVar", dominantRootKindCount: 4);
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        section.TypeTraces.Should().ContainSingle();
        var trace = section.TypeTraces![0];
        trace.SampleCount.Should().Be(5);
        trace.RetainedSampleCount.Should().Be(4);
        trace.StatusLabel.Should().Be("GC root found (4/5 samples, StaticVar)");
    }

    [Fact]
    public void Build_MultiSampleWithNoRetainedSamples_AppendsZeroConsistencyReadToStatusLabel()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: false, sampleCount: 5, retainedSampleCount: 0,
            dominantRootKind: null, dominantRootKindCount: 0);
        var result = Result(retainedSamples: 0, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        section.TypeTraces![0].StatusLabel.Should().Be("No root (0/5 samples)");
    }

    [Fact]
    public void Build_SingleSample_DoesNotAppendConsistencyReadToStatusLabel()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 1, retainedSampleCount: 1,
            dominantRootKind: "Stack", dominantRootKindCount: 1);
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        section.TypeTraces![0].StatusLabel.Should().Be("GC root found");
    }

    [Fact]
    public void Build_DominatorTreeAvailable_ThreadsRetainedBytesThrough()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 1, retainedSampleCount: 1,
            dominantRootKind: "Stack", dominantRootKindCount: 1, retainedBytes: 4_096_000UL);
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        section.TypeTraces![0].RetainedBytes.Should().Be(4_096_000UL);
    }

    [Fact]
    public void Build_DominatorTreeUnavailable_RetainedBytesIsNull()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 1, retainedSampleCount: 1,
            dominantRootKind: "Stack", dominantRootKindCount: 1, retainedBytes: null);
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        section.TypeTraces![0].RetainedBytes.Should().BeNull();
    }

    [Fact]
    public void Build_RootAndLastHopFieldNamesAvailable_ThreadsBothThrough()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 1, retainedSampleCount: 1,
            dominantRootKind: "StaticVar", dominantRootKindCount: 1,
            rootFieldName: "MyApp.Cache._items", lastHopFieldName: "_next");
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        var trace = section.TypeTraces![0];
        trace.RootFieldName.Should().Be("MyApp.Cache._items");
        trace.LastHopFieldName.Should().Be("_next");
    }

    [Fact]
    public void Build_NoFieldNamesResolved_RootAndLastHopFieldNamesAreNull()
    {
        var snapshot = MultiSampleSnapshot(
            hasGcRoot: true, sampleCount: 1, retainedSampleCount: 1,
            dominantRootKind: "StrongHandle", dominantRootKindCount: 1);
        var result = Result(retainedSamples: 1, rootKindDistribution: null, topTypeSampleTraces: [snapshot]);

        var section = _builder.Build(result);

        var trace = section.TypeTraces![0];
        trace.RootFieldName.Should().BeNull();
        trace.LastHopFieldName.Should().BeNull();
    }
}
