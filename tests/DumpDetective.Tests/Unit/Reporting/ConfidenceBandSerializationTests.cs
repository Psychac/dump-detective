using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// The HTML renderer reads <c>confidenceBand</c> instead of re-deriving it from the score
/// (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.4), so every record carrying a
/// confidence the renderer bands must actually emit the key through the source-generated context.
/// </summary>
public sealed class ConfidenceBandSerializationTests
{
    [Theory]
    [InlineData(0.90, "high")]
    [InlineData(0.85, "high")]
    [InlineData(0.70, "medhigh")]
    [InlineData(0.50, "medium")]
    [InlineData(0.20, "low")]
    public void FindingRecord_SerializesConfidenceBand(double score, string expectedBand)
    {
        var finding = new FindingRecord("id", "Analyzer", "Category", "Warning", "Title", null, "Fix", [])
        {
            Confidence = score,
        };

        string json = JsonSerializer.Serialize(new List<FindingRecord> { finding }, ReportJsonContext.Default.Options);

        json.Should().Contain($"\"confidenceBand\":\"{expectedBand}\"");
    }

    [Fact]
    public void FindingRecord_WithoutConfidence_OmitsBandValue()
    {
        var finding = new FindingRecord("id", "Analyzer", "Category", "Warning", "Title", null, "Fix", []);

        string json = JsonSerializer.Serialize(new List<FindingRecord> { finding }, ReportJsonContext.Default.Options);

        json.Should().NotContain("\"confidenceBand\":\"");
    }

    [Fact]
    public void SectionLeadFinding_SerializesScoreBandAndSymbol()
    {
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [],
            LeadFinding: new SectionLeadFinding("Warning", "Lead", "Summary", "Fix", 0.7, []));

        string json = JsonSerializer.Serialize(new List<AnalyzerDetailSection> { section }, ReportJsonContext.Default.Options);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement lead = document.RootElement[0].GetProperty("leadFinding");
        lead.GetProperty("confidenceScore").GetDouble().Should().Be(0.7);
        lead.GetProperty("confidenceBand").GetString().Should().Be("medhigh");
        lead.GetProperty("confidenceSymbol").GetString().Should().Be("●●●○");
    }

    [Fact]
    public void CorrelationEventRecord_SerializesConfidenceBand()
    {
        var correlationEvent = new CorrelationEventRecord("id", "CoOccurrence", "Title", "Rationale", 0.9, [], [], [], []);

        string json = JsonSerializer.Serialize(correlationEvent, ReportJsonContext.Default.Options);

        json.Should().Contain("\"confidenceBand\":\"high\"");
    }
}
