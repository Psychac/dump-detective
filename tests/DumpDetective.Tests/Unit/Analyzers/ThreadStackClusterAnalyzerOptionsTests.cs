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
        var result = new ThreadStackClusterDomainResult(1, 1, 0, 100.0, new[] { "sig" }, null, new[] { artifact });

        result.Artifacts.Should().NotBeNull();
        result.Artifacts!.Count.Should().Be(1);
        result.Artifacts[0].Analyzer.Should().Be("Test");
    }
}
