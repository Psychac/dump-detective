using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// §10.4 (Batch 2b, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// <see cref="DominatorRowMapping"/> and <see cref="DominatorChildIndexBuilder"/>, the two pieces
/// <c>DiskBackedObjectIndexWriter.BuildAndPersistDominatorTree</c> uses to build the dominator child
/// index. Tested independently of <c>ClrHeap</c>/the container format, same fixture style as
/// <c>DominatorTreeComputerTests</c>.
/// </summary>
public class DominatorChildIndexBuilderTests
{
    private static SuccessorsFunc BuildSuccessors(params (ulong Parent, ulong Child)[] edges) =>
        SyntheticSuccessors.Build(edges);

    private static (ReachableGraph Graph, DominatorTreeComputeResult Tree, int[] OldIdToRow) BuildIndexInputs(
        IReadOnlyList<ulong> rootAddresses, SuccessorsFunc successors, Dictionary<ulong, ulong>? shallowSizesByAddress = null)
    {
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            rootAddresses, successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        var methodTables = new ulong[walk.NodeCount];
        var shallowSizes = new ulong[walk.NodeCount];
        var generationTags = new GenerationTag[walk.NodeCount];
        for (int id = 0; id < walk.NodeCount; id++)
        {
            shallowSizes[id] = shallowSizesByAddress?.GetValueOrDefault(walk.Addresses[id], 1UL) ?? 1UL;
            generationTags[id] = GenerationTag.Gen2;
        }

        var graph = new ReachableGraph(walk, methodTables, shallowSizes, generationTags);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);
        int[] oldIdToRow = DominatorRowMapping.Compute(graph, walk.ReachableAddresses);

        return (graph, tree, oldIdToRow);
    }

    private static List<ulong> ChildrenOf(
        ReachableGraph graph, int[] oldIdToRow, DominatorChildIndexBuildResult index, ulong address)
    {
        int oldId = Array.IndexOf(graph.Addresses, address);
        int row = oldIdToRow[oldId];

        var children = new List<ulong>();
        for (int e = index.ChildOffsetsByRow[row]; e < index.ChildOffsetsByRow[row + 1]; e++)
            children.Add(index.ChildAddressesByRow[e]);
        return children;
    }

    [Fact]
    public void Build_DiamondGraph_RootDominatesAllThreeDescendants()
    {
        // root -> a, root -> b, a -> c, b -> c: c is reachable via both branches, so only root
        // dominates it — root's dominator-tree children are a, b, AND c.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL));
        (ReachableGraph graph, DominatorTreeComputeResult tree, int[] oldIdToRow) = BuildIndexInputs([0x1UL], successors);

        DominatorChildIndexBuildResult index = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);

        ChildrenOf(graph, oldIdToRow, index, 0x1UL).Should().BeEquivalentTo([0x2UL, 0x3UL, 0x4UL]);
        ChildrenOf(graph, oldIdToRow, index, 0x2UL).Should().BeEmpty("a doesn't dominate anything");
        ChildrenOf(graph, oldIdToRow, index, 0x4UL).Should().BeEmpty("c has no children of its own");

        index.ChildOffsetsByRow.Length.Should().Be(graph.NodeCount + 1);
    }

    [Fact]
    public void Build_SingleParentLeaf_FoldedLeafAppearsAsOrdinaryChild()
    {
        // root -> a -> leaf: leaf is folded away (out=0, in=1, §D8), but §10.5/§5's whole point is
        // that it must still show up as a's child in the persisted index.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL));
        (ReachableGraph graph, DominatorTreeComputeResult tree, int[] oldIdToRow) = BuildIndexInputs([0x1UL], successors);

        tree.LeafFold.OldToNewId[Array.IndexOf(graph.Addresses, 0x3UL)].Should().Be(-1, "leaf must actually be folded for this test to mean anything");

        DominatorChildIndexBuildResult index = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);

        ChildrenOf(graph, oldIdToRow, index, 0x2UL).Should().BeEquivalentTo([0x3UL], "the folded leaf must appear as a's child");
        ChildrenOf(graph, oldIdToRow, index, 0x1UL).Should().BeEquivalentTo([0x2UL]);
    }

    [Fact]
    public void Build_MultipleFoldedLeavesUnderSameParent_AllAppearAsChildren()
    {
        // hub -> a, hub -> b, hub -> c: a, b, c all fold into hub (out=0, in=1 each).
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x1UL, 0x4UL));
        (ReachableGraph graph, DominatorTreeComputeResult tree, int[] oldIdToRow) = BuildIndexInputs([0x1UL], successors);

        tree.LeafFold.ReducedNodeCount.Should().Be(1, "only the hub survives folding");

        DominatorChildIndexBuildResult index = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);

        ChildrenOf(graph, oldIdToRow, index, 0x1UL).Should().BeEquivalentTo([0x2UL, 0x3UL, 0x4UL]);
    }

    [Fact]
    public void Build_NoDominatorTreeEdgesAtAll_ChildAddressesEmptyButOffsetsStillAligned()
    {
        // Two independent, childless roots — nothing dominates anything.
        var successors = BuildSuccessors();
        (ReachableGraph graph, DominatorTreeComputeResult tree, int[] oldIdToRow) = BuildIndexInputs([0x1UL, 0x2UL], successors);

        DominatorChildIndexBuildResult index = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);

        index.ChildAddressesByRow.Should().BeEmpty();
        index.ChildOffsetsByRow.Length.Should().Be(graph.NodeCount + 1);
        foreach (int offset in index.ChildOffsetsByRow)
            offset.Should().Be(0);
    }

    [Fact]
    public void RowMapping_MatchesSortedAddressOrder()
    {
        var successors = BuildSuccessors((0x30UL, 0x10UL), (0x30UL, 0x20UL));
        (ReachableGraph graph, _, int[] oldIdToRow) = BuildIndexInputs([0x30UL], successors);

        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            [0x30UL], successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
            walk.ReachableAddresses[oldIdToRow[oldId]].Should().Be(graph.Addresses[oldId]);
    }
}
