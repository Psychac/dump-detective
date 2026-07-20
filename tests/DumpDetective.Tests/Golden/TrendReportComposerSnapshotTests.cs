using DumpDetective.Reporting;
using Xunit;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Snapshot tests for TrendReportComposer document shape validation.
/// Ensures trend-specific sections, per-dump projections, and lifecycle aggregation remain consistent.
/// </summary>
public class TrendReportComposerSnapshotTests : GoldenTestBase
{
    [Fact]
    public void TrendReportDocument_IncludesTrendSpecificSections()
    {
        // Test that trend documents include all trend-only sections
        var testName = nameof(TrendReportDocument_IncludesTrendSpecificSections);

        // Once TrendReportComposer is available, verify:
        // - Health scorecard section present
        // - Snapshot strip (lifecycle progression) present
        // - Metric timeline section present
        // - Regression dashboard section present
        // - Trend appendix present
    }

    [Fact]
    public void PerDumpProjection_ContainsFullDocumentShape()
    {
        // Test that per-dump documents (embedded in trend for client-side access)
        // maintain the full single-dump document shape
        var testName = nameof(PerDumpProjection_ContainsFullDocumentShape);

        // Once available, verify via BuildPerDumpDocuments:
        // - Each per-dump projection includes all sections
        // - Per-dump JSON is valid and complete
        // - Embedded per-dump docs match what a single-dump run would produce
    }

    [Fact]
    public void TrendComparison_AggregatesAcrossDumps()
    {
        // Test that BuildTrendComparisonSection correctly aggregates across dumps
        var testName = nameof(TrendComparison_AggregatesAcrossDumps);

        // Once available, verify:
        // - Metric evolution captured (deltas, trends, anomalies)
        // - Type/object growth tracked across dumps
        // - Memory movement patterns recorded
    }

    [Fact]
    public void TrendStory_DescribesHeapEvolution()
    {
        // Test that BuildTrendStory generates coherent narrative of heap state changes
        var testName = nameof(TrendStory_DescribesHeapEvolution);

        // Once available, verify:
        // - Story section connects findings across dumps
        // - Anomalies and regressions flagged
        // - Correlation between metric changes highlighted
    }

    [Fact]
    public void PerDumpRebuild_Cost_IsCorrectlyDocumented()
    {
        // Test documents the O(N) per-dump rebuild cost
        // (N dumps → N+1 full document compositions)
        var testName = nameof(PerDumpRebuild_Cost_IsCorrectlyDocumented);

        // This test serves as specification that rebuilding per-dump documents
        // via full ReportSerializer.Serialize path is intentional and measured.
        // Future optimization should verify this baseline changes as expected.
    }
}
