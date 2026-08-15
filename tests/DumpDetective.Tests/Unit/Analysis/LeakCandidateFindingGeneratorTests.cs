using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class LeakCandidateFindingGeneratorTests
{
    [Fact]
    public void Generate_NoCandidates_ReturnsEmpty()
    {
        var gen = new LeakCandidateFindingGenerator();
        var result = new LeakCandidateDomainResult(0, [], new Dictionary<LeakClass, int>(), HeuristicOnly: true);

        var findings = gen.Generate(result);

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Generate_MultipleCandidates_ReturnsSingleAggregateFinding()
    {
        var gen = new LeakCandidateFindingGenerator();
        var result = BuildResult(
            new LeakCandidateRecord("A.Type", 120_000_000, 1200, 75.0, 94, FindingSeverity.Warning, LeakClass.CacheLeak, null, false, true, 0.8),
            new LeakCandidateRecord("B.Type", 50_000_000, 600, 62.0, 70, FindingSeverity.Warning, LeakClass.CacheLeak, null, false, true, 0.7),
            new LeakCandidateRecord("C.Type", 20_000_000, 220, 54.0, 55, FindingSeverity.Warning, LeakClass.EventLeak, null, false, false, 0.6));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.Title.Should().Contain("cluster");
        finding.Evidence.Should().Contain("3 leak candidate(s)");
        finding.Evidence.Should().Contain("Top examples:");
        finding.Tags.Should().Contain("aggregate");
        finding.Tags.Should().Contain("CacheLeak");
        finding.MetricUnit.Should().Be("bytes");
    }

    [Fact]
    public void Generate_CriticalCandidates_ReturnsSeparateFindingPerCandidate()
    {
        var gen = new LeakCandidateFindingGenerator();
        var result = BuildResult(
            new LeakCandidateRecord("A.Type", 120_000_000, 1200, 75.0, 94, FindingSeverity.Critical, LeakClass.CacheLeak, null, false, true, 0.8),
            new LeakCandidateRecord("B.Type", 50_000_000, 600, 62.0, 70, FindingSeverity.Warning, LeakClass.CacheLeak, null, false, true, 0.7),
            new LeakCandidateRecord("C.Type", 20_000_000, 220, 54.0, 55, FindingSeverity.Warning, LeakClass.EventLeak, null, false, false, 0.6));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.Title.Should().Contain("Critical leak candidate: A.Type");
        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.Tags.Should().Contain("critical");
    }

    [Fact]
    public void Generate_UsesHighestCandidateSeverity()
    {
        var gen = new LeakCandidateFindingGenerator();
        var result = BuildResult(
            new LeakCandidateRecord("A.Type", 10_000_000, 50, 20.0, 45, FindingSeverity.Warning, LeakClass.EventLeak, null, false, false, 0.5),
            new LeakCandidateRecord("B.Type", 5_000_000, 20, 10.0, 35, FindingSeverity.Info, LeakClass.EventLeak, null, false, false, 0.4));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_UsesDominantClassificationRecommendation()
    {
        var gen = new LeakCandidateFindingGenerator();
        var result = BuildResult(
            new LeakCandidateRecord("A.Type", 20_000_000, 100, 30.0, 50, FindingSeverity.Warning, LeakClass.StaticRetention, null, false, false, 0.5),
            new LeakCandidateRecord("B.Type", 30_000_000, 150, 40.0, 60, FindingSeverity.Warning, LeakClass.StaticRetention, null, false, false, 0.5),
            new LeakCandidateRecord("C.Type", 10_000_000, 40, 20.0, 35, FindingSeverity.Info, LeakClass.EventLeak, null, false, false, 0.5));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Recommendation.Should().Contain("static ownership");
    }

    private static LeakCandidateDomainResult BuildResult(params LeakCandidateRecord[] candidates)
    {
        var byClass = new Dictionary<LeakClass, int>();
        for (int i = 0; i < candidates.Length; i++)
        {
            LeakClass key = candidates[i].Classification;
            if (!byClass.TryGetValue(key, out int count)) count = 0;
            byClass[key] = count + 1;
        }

        return new LeakCandidateDomainResult(
            TotalCandidates: candidates.Length,
            TopCandidates: candidates,
            CandidatesByClass: byClass,
            HeuristicOnly: true);
    }
}
