using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ThreadStackClusterFindingGeneratorTests
{
    [Fact]
    public void Generate_DominantClusterAboveThreshold_EmitsWarning()
    {
        var gen = new ThreadStackClusterFindingGenerator();
        var dominant = new ThreadClusterSnapshot(600, [], "MyApp.Worker.Run()");
        var result = new ThreadStackClusterDomainResult(1000, 5, 0, 0.5, ["MyApp.Worker.Run()"], TopClusters: [dominant]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("dominant-cluster")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Tags.Should().Contain("hotspot");
    }

    [Fact]
    public void Generate_DominantClusterMatchesFrameworkPattern_DowngradesToInfo()
    {
        var gen = new ThreadStackClusterFindingGenerator();
        var dominant = new ThreadClusterSnapshot(600, [], "<No managed frames> (Threadpool)", FrameworkPattern: "Threadpool-idle");
        var result = new ThreadStackClusterDomainResult(1000, 5, 0, 0.5, ["<No managed frames> (Threadpool)"], TopClusters: [dominant]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("dominant-cluster")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
        finding.Tags.Should().Contain("framework-pattern");
        finding.Recommendation.Should().Contain("Threadpool-idle");
    }
}
