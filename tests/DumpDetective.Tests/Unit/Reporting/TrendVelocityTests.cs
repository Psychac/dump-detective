using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Models;
using FluentAssertions;
using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Unit.Reporting;

public class TrendVelocityTests
{
    [Fact]
    public void TrendHealthScorecardBuilder_ComputesVelocityAndVolatility()
    {
        // Build three snapshots with increasing severity for Memory domain
        var s0 = new AnalysisSnapshot(0, "dump0.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Info, "t0", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);
        var s1 = new AnalysisSnapshot(1, "dump1.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Warning, "t1", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);
        var s2 = new AnalysisSnapshot(2, "dump2.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Critical, "t2", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);

        var snapshots = new[] { s0, s1, s2 };
        var scorecard = TrendHealthScorecardBuilder.Build(snapshots);

        scorecard.Should().NotBeNull();
        scorecard.Domains.Should().ContainKey("Memory");
        var memoryEntry = scorecard.Domains["Memory"];
        memoryEntry.VelocityScore.Should().HaveValue();
        memoryEntry.VelocityScore!.Value.Should().BeGreaterThan(0);
        memoryEntry.VolatilityScore.Should().HaveValue();
        memoryEntry.VolatilityScore!.Value.Should().BeGreaterOrEqualTo(0);
        memoryEntry.ConfidenceTrend.Should().NotBeNull();
    }

    [Fact]
    public void HealthScorecard_SerializesAndDeserializes_PreservingVelocity()
    {
        var s0 = new AnalysisSnapshot(0, "dump0.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Info, "t0", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);
        var s1 = new AnalysisSnapshot(1, "dump1.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Warning, "t1", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);
        var s2 = new AnalysisSnapshot(2, "dump2.dmp", Array.Empty<AnalyzerRunResult>(),
            new[] { new InsightFinding("MemoryAnalyzer", "X", FindingSeverity.Critical, "t2", "e", "r", new string[0]) },
            new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);

        var snapshots = new[] { s0, s1, s2 };
        var scorecard = TrendHealthScorecardBuilder.Build(snapshots);

        var opts = new JsonSerializerOptions { WriteIndented = false };
        string json = JsonSerializer.Serialize(scorecard, opts);
        var des = JsonSerializer.Deserialize<HealthScorecard>(json, opts);

        des.Should().NotBeNull();
        des!.Domains.Should().ContainKey("Memory");
        var mem = des.Domains["Memory"];
        mem.VelocityScore.Should().HaveValue();
        mem.VolatilityScore.Should().HaveValue();
    }
}
