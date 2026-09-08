using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ObjectShapeTrendComparerTests
{
    private static ObjectShapeAnalyzerDomainResult MakeResult(long totalGcScanWork, long totalGen2GcScanWork) =>
        new(
            TopReferenceHeavyTypes: [],
            TopValueHeavyTypes: [],
            TopBalancedTypes: [],
            TotalTypesAnalyzed: 5,
            AvgRefFieldsPerType: 3,
            TotalGcScanWork: totalGcScanWork,
            TopGen2RetainedTypes: [],
            TotalGen2GcScanWork: totalGen2GcScanWork);

    [Fact]
    public void ExtractMetrics_EmitsTotalGen2GcScanWork()
    {
        var result = MakeResult(totalGcScanWork: 10_000, totalGen2GcScanWork: 4_000);

        var metrics = new ObjectShapeTrendComparer().ExtractMetrics(result);

        metrics.Should().Contain(m => m.Key == "shape.total.gen2.gc.scan.work" && m.Value == 4_000);
    }

    [Fact]
    public void Compare_EmitsTotalGen2GcScanWorkDelta()
    {
        var baseline = MakeResult(totalGcScanWork: 10_000, totalGen2GcScanWork: 2_000);
        var current = MakeResult(totalGcScanWork: 15_000, totalGen2GcScanWork: 6_000);

        var deltas = new ObjectShapeTrendComparer().Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "shape.total.gen2.gc.scan.work" && d.Delta == 4_000);
    }
}
