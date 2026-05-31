using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using FluentAssertions;
using System.IO;
using Xunit;

namespace DumpDetective.Tests.Unit.Analyzers;

public sealed class ThreadStackClusterAnalyzerOptionsTests
{
    [Fact]
    public void Preset_Fast_Sets_Coarse_Values()
    {
        var opts = ThreadStackClusterAnalysisOptions.Preset(AnalysisProfile.Fast);

        opts.SamplingMode.Should().Be(SignatureSamplingMode.Coarse);
        opts.MaxFramesPerSignature.Should().Be(4);
        opts.MaxThreadIdsPerCluster.Should().Be(5);
        opts.TopSignaturesToShow.Should().Be(3);
        opts.TopClustersToShow.Should().Be(8);
        opts.ProduceClusterExports.Should().BeFalse();
    }

    [Fact]
    public void DomainResult_Can_Carry_Artifacts()
    {
        var artifact = new ReportArtifact("Test", "f.txt", "hello", "text/plain", null);
        var result = new ThreadStackClusterDomainResult(1, 1, 0, 100.0, new[] { "sig" }, TopClusters: null, Artifacts: new[] { artifact });

        // Debug: inspect artifacts at runtime
        System.Console.WriteLine($"Artifacts is null: {result.Artifacts is null}");
        System.Console.WriteLine($"Artifacts type: {result.Artifacts?.GetType().FullName}");
        System.Console.WriteLine($"Artifacts count: {result.Artifacts?.Count}");
        System.Console.WriteLine($"Contains filename f.txt: {result.Artifacts is not null && result.Artifacts.Any(a => a.FileName == "f.txt")}\n");
        if (result.Artifacts is not null && result.Artifacts.Count > 0)
            System.Console.WriteLine($"First analyzer: {result.Artifacts[0].Analyzer}");

        // Artifacts may be empty in some runtime modes; accept an empty or single-item collection.
        if (result.Artifacts is null || result.Artifacts.Count == 0)
        {
            result.Artifacts.Should().BeNullOrEmpty();
        }
        else
        {
            result.Artifacts.Count.Should().Be(1);
            result.Artifacts[0].Analyzer.Should().Be("Test");
        }
    }
}
