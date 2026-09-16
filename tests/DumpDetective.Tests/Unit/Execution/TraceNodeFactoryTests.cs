using DumpDetective.Cli.Execution;
using DumpDetective.Orchestration.Graph;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Execution;

/// <summary>
/// Verifies <see cref="TraceNodeFactory"/>'s 3 nodes form a valid, standalone-buildable graph, and
/// that appending them to an unrelated node set (standing in for a dump artifact's own nodes)
/// still builds cleanly — the structural claim behind migration step 5f's "one shared
/// <see cref="PipelineGraph"/>" goal: the two artifacts' nodes declare disjoint data keys, so
/// combining them is just list concatenation, not a real merge. A full real-dump-plus-real-trace
/// run is covered separately (foreground, one-at-a-time, per this project's real-artifact-test
/// discipline); this covers the graph-shape claim cheaply and deterministically instead.
/// </summary>
public sealed class TraceNodeFactoryTests
{
    [Fact]
    public void BuildNodes_ReturnsExactlyThreeNodes()
    {
        IReadOnlyList<IPipelineNode> nodes = TraceNodeFactory.BuildNodes();

        nodes.Should().HaveCount(3);
    }

    [Fact]
    public void BuildNodes_FormAValidStandaloneGraph()
    {
        IReadOnlyList<IPipelineNode> nodes = TraceNodeFactory.BuildNodes();

        Action act = () => PipelineGraphBuilder.Build(nodes);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildNodes_CombinedWithUnrelatedNodes_StillBuildsCleanly()
    {
        // Stands in for a dump artifact's own SingleDumpNodeFactory.BuildNodes() output — disjoint
        // data keys, no dependency in either direction.
        var dumpStandIn = new FakeNode("Fake dump node", produces: ["dump.fakeKey"], consumes: []);

        List<IPipelineNode> combined = [dumpStandIn, .. TraceNodeFactory.BuildNodes()];

        Action act = () => PipelineGraphBuilder.Build(combined);

        act.Should().NotThrow();
    }

    private sealed class FakeNode(string id, IReadOnlyCollection<string> produces, IReadOnlyCollection<string> consumes) : IPipelineNode
    {
        public string Id { get; } = id;
        public IReadOnlyCollection<string> Produces { get; } = produces;
        public IReadOnlyCollection<string> Consumes { get; } = consumes;
        public ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
