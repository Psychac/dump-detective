using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public class ReachableGraphWalkerTests
{
    private static SuccessorsFunc BuildSuccessors(params (ulong Parent, ulong Child)[] edges) =>
        SyntheticSuccessors.Build(edges);

    [Fact]
    public void Walk_AssignsDenseIdsAndBuildsForwardCsr()
    {
        // root(0x10) -> a(0x20) -> b(0x30)
        var successors = BuildSuccessors((0x10UL, 0x20UL), (0x20UL, 0x30UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x10UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.CapExceeded.Should().BeFalse();
        result.NodeCount.Should().Be(3);
        result.Addresses.Should().BeEquivalentTo([0x10UL, 0x20UL, 0x30UL]);
    }

    [Fact]
    public void Walk_DiamondGraph_ProducesCorrectDegreesAndCsr()
    {
        // root -> a, root -> b, a -> c, b -> c
        var successors = BuildSuccessors(
            (0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.NodeCount.Should().Be(4);

        int rootId = Array.IndexOf(result.Addresses, 0x1UL);
        int cId = Array.IndexOf(result.Addresses, 0x4UL);

        result.OutDegree[rootId].Should().Be(2);
        result.InDegree[cId].Should().Be(2, "c is reachable via both a and b");

        // Reverse CSR for c should list both predecessors.
        var predecessorsOfC = new List<ulong>();
        for (int e = result.RevOffsets[cId]; e < result.RevOffsets[cId + 1]; e++)
            predecessorsOfC.Add(result.Addresses[result.RevTargets[e]]);
        predecessorsOfC.Should().BeEquivalentTo([0x2UL, 0x3UL]);
    }

    [Fact]
    public void Walk_MultipleRoots_AllReachableFromEitherRoot()
    {
        var successors = BuildSuccessors((0x1UL, 0x3UL), (0x2UL, 0x3UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL, 0x2UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Walk_CycleViaBackEdge_TerminatesAndDoesNotDuplicateNodes()
    {
        // root -> a -> b -> a (back edge)
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x2UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Walk_UnreachableNode_NeverAppearsInGraph()
    {
        // root -> a. b exists in the successors map but nothing points to it from the root.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x99UL, 0x3UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.NodeCount.Should().Be(2);
        result.Addresses.Should().NotContain(0x3UL);
    }

    [Fact]
    public void Walk_ZeroAddressSkippedAsRootAndAsChild()
    {
        var successors = BuildSuccessors((0x1UL, 0x0UL), (0x1UL, 0x2UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL, 0x0UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.NodeCount.Should().Be(2);
        result.Addresses.Should().BeEquivalentTo([0x1UL, 0x2UL]);
    }

    /// <summary>Node-only budget (zero bytes/edge) — reproduces the old flat node-cap semantics.</summary>
    private static ExactDominatorTreeBudget NodeBudget(int maxNodes) =>
        new(maxBytes: maxNodes * 100L, bytesPerNode: 100, bytesPerEdge: 0);

    /// <summary>Edge-only budget (zero bytes/node) — isolates the term the old node cap couldn't see.</summary>
    private static ExactDominatorTreeBudget EdgeBudget(int maxEdges) =>
        new(maxBytes: maxEdges * 100L, bytesPerNode: 0, bytesPerEdge: 100);

    [Fact]
    public void Walk_ReachablePopulationExceedsNodeBudget_ReturnsCappedResult()
    {
        // root -> a -> b -> c: a 2-node budget should trip while discovering the 3rd node.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x4UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, NodeBudget(2), CancellationToken.None);

        result.CapExceeded.Should().BeTrue();
        result.NodeCount.Should().Be(0, "a capped result carries no partial graph data");
    }

    [Fact]
    public void Walk_ReachablePopulationUnderNodeBudget_SucceedsNormally()
    {
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, NodeBudget(10), CancellationToken.None);

        result.CapExceeded.Should().BeFalse();
        result.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Walk_DenseGraphExceedsEdgeBudget_ReturnsCappedResult()
    {
        // A hub with 5 children: only 6 nodes, but 5 edges. The pre-existing node-only cap could not
        // express this — a dense graph can blow the memory budget on its edge arrays while its node
        // count still looks comfortable, which is the whole reason the budget model has an edge term.
        var successors = BuildSuccessors(
            (0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x1UL, 0x4UL), (0x1UL, 0x5UL), (0x1UL, 0x6UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, EdgeBudget(3), CancellationToken.None);

        result.CapExceeded.Should().BeTrue();
        result.NodeCount.Should().Be(0);
    }

    [Fact]
    public void Walk_DenseGraphWithinEdgeBudget_SucceedsNormally()
    {
        var successors = BuildSuccessors(
            (0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x1UL, 0x4UL), (0x1UL, 0x5UL), (0x1UL, 0x6UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, EdgeBudget(50), CancellationToken.None);

        result.CapExceeded.Should().BeFalse();
        result.NodeCount.Should().Be(6);
        result.FwdTargets.Length.Should().Be(5);
    }

    [Fact]
    public void Walk_UnlimitedBudget_NeverCaps()
    {
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x4UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        result.CapExceeded.Should().BeFalse();
        result.NodeCount.Should().Be(4);
    }
}
