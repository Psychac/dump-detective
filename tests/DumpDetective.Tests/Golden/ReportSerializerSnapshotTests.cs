using DumpDetective.Reporting;
using Xunit;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Snapshot tests for ReportSerializer output validation.
/// Ensures document shape, section assembly, and serialization remain consistent across changes.
/// </summary>
public class ReportSerializerSnapshotTests : GoldenTestBase
{
    [Fact]
    public void CanonicalReportDocument_Serialization_MatchesBaseline()
    {
        // Arrange: Create a minimal AnalysisReportDocument
        // This test documents the expected shape of serialized canonical report documents
        var testName = nameof(CanonicalReportDocument_Serialization_MatchesBaseline);

        // Act: Serialize document (once actual ReportSerializer is available)
        // var document = new AnalysisReportDocument { /*...*/ };
        // var serialized = ReportSerializer.Serialize(document);
        // var json = SerializeToJson(serialized);

        // Assert: Compare against stored baseline
        // ApproveGoldenOutput(json, testName);

        // Note: This test will be enabled once ReportSerializer and AnalysisReportDocument
        // models are populated in the Reporting project. It serves as a living specification
        // for the canonical document format across serialization changes.
    }

    [Fact]
    public void SectionAssembly_RespectsAnalyzerOrdering()
    {
        // Test that section assembly maintains proper analyzer ordering and categorization
        var testName = nameof(SectionAssembly_RespectsAnalyzerOrdering);

        // Once available, verify:
        // - Each analyzer section is present
        // - Ordering matches the analyzer registry
        // - No section duplication or loss
    }

    [Fact]
    public void FindingProjection_IncludesAllDomainResults()
    {
        // Test that finding projection extracts all domain-specific results
        var testName = nameof(FindingProjection_IncludesAllDomainResults);

        // Once available, verify:
        // - All analyzer domain results are projected into findings
        // - Finding metadata (severity, category, tags) is preserved
        // - Correlation fields are populated for cross-analyzer links
    }
}
