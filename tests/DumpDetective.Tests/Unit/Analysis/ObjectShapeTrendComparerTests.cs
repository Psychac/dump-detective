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
    public void ExtractMetrics_NoScopedProfiles_EmitsNothing()
    {
        // Headline metrics (shape.total.gen2.gc.scan.work and the rest) retired onto
        // ObservationTrendDiffer — see ObservationDifferAnalyzerOwnership. With no
        // TopReferenceHeavyTypes/TopBalancedTypes/TopValueHeavyTypes entries, the comparer now
        // has nothing scoped left to emit either.
        var result = MakeResult(totalGcScanWork: 10_000, totalGen2GcScanWork: 4_000);

        var metrics = new ObjectShapeTrendComparer().ExtractMetrics(result);

        metrics.Should().BeEmpty();
    }

    [Fact]
    public void Compare_AlwaysEmitsNothing()
    {
        // Compare() had no pre-existing scoped delta branches (only ExtractMetrics fed
        // per-type ratios into the timeline), so it always returns empty now that the headline
        // branches are gone — covered by the differ instead.
        var baseline = MakeResult(totalGcScanWork: 10_000, totalGen2GcScanWork: 2_000);
        var current = MakeResult(totalGcScanWork: 15_000, totalGen2GcScanWork: 6_000);

        var deltas = new ObjectShapeTrendComparer().Compare(baseline, current);

        deltas.Should().BeEmpty();
    }
}
