using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ObjectShapeSectionBuilderTests
{
    private readonly ObjectShapeSectionBuilder _builder = new();

    private static TypeShapeProfile Profile(string name, int refFields, ulong instanceCount, ulong gen2InstanceCount) =>
        new(name, TotalFields: refFields + 1, ReferenceFields: refFields, ValueFields: 1, ReferenceFieldRatio: 0.9,
            InstanceCount: instanceCount, TotalSize: instanceCount * 24, IsFinalizable: false, IsValueType: false,
            IsArray: false, BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ReferenceHeavy,
            Gen2InstanceCount: gen2InstanceCount);

    private static ObjectShapeAnalyzerDomainResult Result(
        IReadOnlyList<TypeShapeProfile> topGen2RetainedTypes, long totalGen2GcScanWork) =>
        new(
            TopReferenceHeavyTypes: [Profile("Demo.Widget", refFields: 5, instanceCount: 1_000, gen2InstanceCount: 400)],
            TopValueHeavyTypes: [],
            TopBalancedTypes: [],
            TotalTypesAnalyzed: 3,
            AvgRefFieldsPerType: 5,
            TotalGcScanWork: 5_000,
            TopGen2RetainedTypes: topGen2RetainedTypes,
            TotalGen2GcScanWork: totalGen2GcScanWork);

    [Fact]
    public void Build_AlwaysAddsTotalGen2GcScanWorkKeyMetric()
    {
        var result = Result(topGen2RetainedTypes: [], totalGen2GcScanWork: 2_000);

        var section = _builder.Build(result);

        section.KeyMetrics.Should().ContainKey("total_gen2_gc_scan_work");
        section.KeyMetrics!["total_gen2_gc_scan_work"].Should().BeOfType<NumericMetricValue>()
            .Which.Value.Should().Be(2_000d);
    }

    [Fact]
    public void Build_ReferenceHeavyTable_IncludesGen2Columns()
    {
        var result = Result(topGen2RetainedTypes: [], totalGen2GcScanWork: 0);

        var section = _builder.Build(result);

        var table = section.CompactTables!.Should().ContainSingle(t => t.Title == "Reference-heavy types").Subject;
        table.Headers.Should().Contain(h => h.Name == "Gen2 Instances");
        table.Headers.Should().Contain(h => h.Name == "Gen2 Scan Cost");

        int gen2InstancesIdx = table.Headers.ToList().FindIndex(h => h.Name == "Gen2 Instances");
        int gen2ScanCostIdx = table.Headers.ToList().FindIndex(h => h.Name == "Gen2 Scan Cost");
        table.Rows[0].Values[gen2InstancesIdx].Should().Be(400L);
        table.Rows[0].Values[gen2ScanCostIdx].Should().Be(2_000L); // 5 ref fields * 400 Gen2 instances
    }

    [Fact]
    public void Build_WithGen2RetainedTypes_AddsGen2RetainedTable()
    {
        var gen2Type = Profile("Demo.Retained", refFields: 8, instanceCount: 50_000, gen2InstanceCount: 45_000);
        var result = Result(topGen2RetainedTypes: [gen2Type], totalGen2GcScanWork: 360_000);

        var section = _builder.Build(result);

        section.CompactTables.Should().Contain(t => t.Title == "Gen2-retained types (retention-adjusted GC scan cost)");
    }

    [Fact]
    public void Build_NoGen2RetainedTypes_OmitsGen2RetainedTable()
    {
        var result = Result(topGen2RetainedTypes: [], totalGen2GcScanWork: 0);

        var section = _builder.Build(result);

        (section.CompactTables ?? []).Should().NotContain(t => t.Title == "Gen2-retained types (retention-adjusted GC scan cost)");
    }
}
