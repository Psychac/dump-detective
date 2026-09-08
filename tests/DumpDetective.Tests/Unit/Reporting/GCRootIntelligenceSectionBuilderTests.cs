using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;
using DumpDetective.Reporting.Serialization;

using FluentAssertions;

using System.Text.Json;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// Regression coverage for the real-dump "possible object cycle detected" JSON failure:
/// <see cref="RootOwnedSubgraphFinding.SubgraphTypeNames"/> comes from a breadth-first walk capped
/// at 500 *nodes* (not 20 hops), so for a near-linear reachable subgraph (e.g. a long List/
/// LinkedList chain) it can carry close to 500 hops. <c>GCRootIntelligenceSectionBuilder</c> merges
/// these into a shared-prefix trie for its "Root-owned subgraph shapes" TreeWidget; an unbranched
/// run of that length previously nested hundreds of levels of <see cref="TreeNode.Children"/>,
/// exceeding <c>System.Text.Json</c>'s serializer <c>MaxDepth</c>.
/// </summary>
public sealed class GCRootIntelligenceSectionBuilderTests
{
    private static GCRootDomainResult BuildResult(IReadOnlyList<string> subgraphTypeNames) =>
        new(
            TotalRoots: 1,
            ByKind: [],
            TopRootsBySeverity: [],
            RootOwnedSubgraphs:
            [
                new RootOwnedSubgraphFinding(
                    TargetAddress: 0x1000,
                    TargetTypeName: subgraphTypeNames[0],
                    RootKind: "Static",
                    SubgraphTypeNames: subgraphTypeNames,
                    SubgraphNodeCount: subgraphTypeNames.Count,
                    WasCapped: false)
            ],
            SubgraphWalkCapped: false,
            SubgraphWalkCappedCount: 0);

    private static IReadOnlyList<string> LongUnbranchedChain(int hopCount)
    {
        var names = new string[hopCount];
        for (int i = 0; i < hopCount; i++)
            names[i] = $"App.Node{i}";
        return names;
    }

    [Fact]
    public void Build_UnbranchedChainBeyondSafetyBound_CapsTreeNodeNestingDepth()
    {
        // 300 hops — comfortably beyond BoundedGraphWalk's 20-hop docs comment, and well past
        // the 64-level rendering cap this test guards against regressing.
        GCRootDomainResult result = BuildResult(LongUnbranchedChain(300));

        AnalyzerDetailSection section = new GCRootIntelligenceSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title.StartsWith("Root-owned subgraph shapes"));
        widget.AnyTruncated.Should().BeTrue();

        TreeNode node = widget.Roots.Single();
        int nestingDepth = 0;
        while (node.Children is { Count: 1 })
        {
            node = node.Children[0];
            nestingDepth++;
        }

        nestingDepth.Should().BeLessThanOrEqualTo(65, "the section builder must cap chain depth, not just node count/breadth");
        node.TruncatedChildCount.Should().BeGreaterThan(0, "the chain was cut off before reaching the real leaf");
        node.Children.Should().BeNull();
    }

    [Fact]
    public void Build_UnbranchedChainBeyondSafetyBound_SerializesWithoutCycleException()
    {
        GCRootDomainResult result = BuildResult(LongUnbranchedChain(300));
        AnalyzerDetailSection section = new GCRootIntelligenceSectionBuilder().Build(result);

        Action act = () => JsonSerializer.Serialize(
            new List<AnalyzerDetailSection> { section }, ReportJsonContext.Default.Options);

        act.Should().NotThrow<JsonException>();
    }

    [Fact]
    public void Build_ShortPath_RendersFullUnTruncatedChain()
    {
        GCRootDomainResult result = BuildResult(LongUnbranchedChain(5));

        AnalyzerDetailSection section = new GCRootIntelligenceSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title.StartsWith("Root-owned subgraph shapes"));
        widget.AnyTruncated.Should().BeFalse();

        TreeNode node = widget.Roots.Single();
        int nestingDepth = 0;
        while (node.Children is { Count: 1 })
        {
            node = node.Children[0];
            nestingDepth++;
        }

        nestingDepth.Should().Be(4);
        node.TruncatedChildCount.Should().Be(0);
    }
}
