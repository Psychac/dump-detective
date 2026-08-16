using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public class ReachableGraphWalkerTests
{
    /// <summary>Builds a <c>successors</c> function from a plain edge list, mirroring the
    /// <c>BuildGraph</c> helper style already established in <c>LengauerTarjanTests</c>.</summary>
    private static Func<ulong, IEnumerable<ulong>> BuildSuccessors(params (ulong Parent, ulong Child)[] edges)
    {
        var forward = new Dictionary<ulong, List<ulong>>();
        foreach ((ulong parent, ulong child) in edges)
        {
            if (!forward.TryGetValue(parent, out List<ulong>? children))
                forward[parent] = children = new List<ulong>();
            children.Add(child);
        }

        return addr => forward.TryGetValue(addr, out List<ulong>? c) ? c : Array.Empty<ulong>();
    }

    [Fact]
    public void Walk_AssignsDenseIdsAndBuildsForwardCsr()
    {
        // root(0x10) -> a(0x20) -> b(0x30)
        var successors = BuildSuccessors((0x10UL, 0x20UL), (0x20UL, 0x30UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x10UL], successors, nodeCap: 0, CancellationToken.None);

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

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, nodeCap: 0, CancellationToken.None);

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

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL, 0x2UL], successors, nodeCap: 0, CancellationToken.None);

        result.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Walk_CycleViaBackEdge_TerminatesAndDoesNotDuplicateNodes()
    {
        // root -> a -> b -> a (back edge)
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x2UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, nodeCap: 0, CancellationToken.None);

        result.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Walk_UnreachableNode_NeverAppearsInGraph()
    {
        // root -> a. b exists in the successors map but nothing points to it from the root.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x99UL, 0x3UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, nodeCap: 0, CancellationToken.None);

        result.NodeCount.Should().Be(2);
        result.Addresses.Should().NotContain(0x3UL);
    }

    [Fact]
    public void Walk_ZeroAddressSkippedAsRootAndAsChild()
    {
        var successors = BuildSuccessors((0x1UL, 0x0UL), (0x1UL, 0x2UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL, 0x0UL], successors, nodeCap: 0, CancellationToken.None);

        result.NodeCount.Should().Be(2);
        result.Addresses.Should().BeEquivalentTo([0x1UL, 0x2UL]);
    }

    [Fact]
    public void Walk_ReachablePopulationExceedsCap_ReturnsCappedResult()
    {
        // root -> a -> b -> c: cap of 2 should trip while discovering the 3rd node.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x4UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, nodeCap: 2, CancellationToken.None);

        result.CapExceeded.Should().BeTrue();
        result.NodeCount.Should().Be(0, "a capped result carries no partial graph data");
    }

    [Fact]
    public void Walk_ReachablePopulationUnderCap_SucceedsNormally()
    {
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL));

        ReachableGraphWalkResult result = ReachableGraphWalker.Walk([0x1UL], successors, nodeCap: 10, CancellationToken.None);

        result.CapExceeded.Should().BeFalse();
        result.NodeCount.Should().Be(3);
    }
}
