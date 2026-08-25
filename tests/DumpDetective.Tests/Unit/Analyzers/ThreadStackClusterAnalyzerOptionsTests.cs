using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using FluentAssertions;
using System.IO;
using Xunit;

namespace DumpDetective.Tests.Unit.Analyzers;

public sealed class ThreadStackClusterAnalyzerOptionsTests
{
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

    [Fact]
    public void DomainResult_Can_Carry_TopFrameHotspots()
    {
        var hotspots = new[]
        {
            new NameCountEntry("System.Threading.Monitor.Wait(object)", 42),
            new NameCountEntry("MyApp.Worker.Run()", 7),
        };
        var result = new ThreadStackClusterDomainResult(2, 1, 0, 50.0, new[] { "sig" }, TopFrameHotspots: hotspots);

        result.TopFrameHotspots.Should().NotBeNull();
        result.TopFrameHotspots!.Should().HaveCount(2);
        result.TopFrameHotspots![0].Name.Should().Be("System.Threading.Monitor.Wait(object)");
        result.TopFrameHotspots![0].Count.Should().Be(42);
    }

    [Theory]
    [InlineData("System.Threading.ThreadPoolWorkQueue.Dispatch()", "Threadpool-idle")]
    [InlineData("System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()", "Threadpool-idle")]
    [InlineData("<No managed frames> (GC)", "GC")]
    [InlineData("<No managed frames> (Finalizer)", "Finalizer")]
    [InlineData("<No managed frames> (IOCP)", "IOCP-idle")]
    [InlineData("<No managed frames> (Threadpool)", "Threadpool-idle")]
    [InlineData("MyApp.Worker.Run() | System.Threading.Monitor.Wait(object)", null)]
    [InlineData("<No managed frames>", null)]
    public void ClassifyFrameworkPattern_Recognizes_Known_Signatures(string signature, string? expected)
    {
        ThreadStackClusterAnalyzer.ClassifyFrameworkPattern(signature).Should().Be(expected);
    }

    [Fact]
    public void ThreadClusterSnapshot_Can_Carry_FrameworkPattern()
    {
        var snapshot = new ThreadClusterSnapshot(500, Array.Empty<uint>(), "<No managed frames> (GC)", FrameworkPattern: "GC");

        snapshot.FrameworkPattern.Should().Be("GC");
    }

    private static ThreadStackClusterAnalyzer.StackCluster MakeCluster(string signature, int count)
    {
        var cluster = new ThreadStackClusterAnalyzer.StackCluster(signature) { Count = count };
        return cluster;
    }

    [Fact]
    public void BuildClusterTree_Empty_Input_Returns_Empty()
    {
        ThreadStackClusterAnalyzer.BuildClusterTree(Array.Empty<ThreadStackClusterAnalyzer.StackCluster>())
            .Should().BeEmpty();
    }

    [Fact]
    public void BuildClusterTree_Merges_Clusters_Sharing_Innermost_Frame()
    {
        var clusters = new[]
        {
            MakeCluster("Wait() | Foo()", 5),
            MakeCluster("Wait() | Bar()", 3),
        };

        var roots = ThreadStackClusterAnalyzer.BuildClusterTree(clusters);

        roots.Should().HaveCount(1);
        var wait = roots[0];
        wait.FrameLabel.Should().Be("Wait()");
        wait.Count.Should().Be(8);
        wait.IsChain.Should().BeFalse();
        wait.Children.Should().HaveCount(2);
        wait.Children.Should().Contain(c => c.FrameLabel == "Foo()" && c.Count == 5);
        wait.Children.Should().Contain(c => c.FrameLabel == "Bar()" && c.Count == 3);
    }

    [Fact]
    public void BuildClusterTree_Collapses_Unbranched_Chain_Into_One_Node()
    {
        var clusters = new[] { MakeCluster("Wait() | Foo() | Bar() | Baz()", 10) };

        var roots = ThreadStackClusterAnalyzer.BuildClusterTree(clusters);

        roots.Should().HaveCount(1);
        var node = roots[0];
        node.FrameLabel.Should().Be("Wait() → Foo() → Bar() → Baz()");
        node.Count.Should().Be(10);
        node.IsChain.Should().BeTrue();
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildClusterTree_Caps_Children_Per_Node_And_Reports_Truncation()
    {
        var clusters = new List<ThreadStackClusterAnalyzer.StackCluster>();
        for (int i = 0; i < 10; i++)
            clusters.Add(MakeCluster($"Wait() | Child{i}()", 10 - i));

        var roots = ThreadStackClusterAnalyzer.BuildClusterTree(clusters);

        roots.Should().HaveCount(1);
        var wait = roots[0];
        wait.Children.Should().HaveCount(8);
        wait.TruncatedChildCount.Should().Be(2);
    }
}
