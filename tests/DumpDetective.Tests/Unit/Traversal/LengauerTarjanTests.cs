using DumpDetective.Analysis.Traversal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal;

public class LengauerTarjanTests
{
    /// <summary>
    /// Builds forward/backward neighbor functions from a dense-int-id edge list (parent -> child),
    /// mirroring <see cref="BidirectionalGraphSearchTests"/>'s helper. Backward is the exact
    /// inverse — matching the design doc's requirement that LT's predecessor function be
    /// true/uncapped, never a fanout-truncated index.
    /// </summary>
    private static (Func<int, IEnumerable<int>> Successors, Func<int, IEnumerable<int>> Predecessors) BuildGraph(
        int nodeCount, params (int Parent, int Child)[] edges)
    {
        var forward = new List<int>[nodeCount];
        var backward = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            forward[i] = new List<int>();
            backward[i] = new List<int>();
        }

        foreach ((int parent, int child) in edges)
        {
            forward[parent].Add(child);
            backward[child].Add(parent);
        }

        return (n => forward[n], n => backward[n]);
    }

    [Fact]
    public void Chain_EachNodeDominatedByItsSinglePredecessor()
    {
        // root(0) -> a(1) -> b(2) -> c(3)
        var (succ, pred) = BuildGraph(4, (0, 1), (1, 2), (2, 3));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(4, root: 0, succ, pred);

        idom[0].Should().Be(0, "the root has no dominator above it, by convention idom[root] == root");
        idom[1].Should().Be(0);
        idom[2].Should().Be(1);
        idom[3].Should().Be(2);
    }

    [Fact]
    public void Diamond_MergePointDominatedByCommonAncestor_NotEitherBranch()
    {
        // root(0) -> a(1), root(0) -> b(2), a(1) -> c(3), b(2) -> c(3)
        // c is reachable via two disjoint branches, so neither a nor b dominates it — only root does.
        var (succ, pred) = BuildGraph(4, (0, 1), (0, 2), (1, 3), (2, 3));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(4, root: 0, succ, pred);

        idom[1].Should().Be(0);
        idom[2].Should().Be(0);
        idom[3].Should().Be(0, "c is reachable via both a and b, so no node besides root dominates it");
    }

    [Fact]
    public void NestedDiamonds_MultiLevelMergeStillResolvesToTrueCommonDominator()
    {
        // root(0) -> a(1), root(0) -> b(2)
        // a(1) -> c(3), a(1) -> d(4)
        // b(2) -> c(3), b(2) -> d(4)
        // c(3) -> e(5), d(4) -> e(5)
        // e(5) -> f(6)
        // A naive "look at immediate predecessors only" heuristic could mistakenly attribute e's
        // dominator to c or d; the true answer is root, since every path to e can route through
        // either c or d independently.
        var (succ, pred) = BuildGraph(
            7,
            (0, 1), (0, 2),
            (1, 3), (1, 4),
            (2, 3), (2, 4),
            (3, 5), (4, 5),
            (5, 6));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(7, root: 0, succ, pred);

        idom[1].Should().Be(0);
        idom[2].Should().Be(0);
        idom[3].Should().Be(0, "c is reachable via both a and b");
        idom[4].Should().Be(0, "d is reachable via both a and b");
        idom[5].Should().Be(0, "e is reachable via both c-only and d-only paths, so neither dominates it");
        idom[6].Should().Be(5, "f has a single predecessor, e, which trivially dominates it");
    }

    [Fact]
    public void BackEdge_DoesNotDisturbDominanceAlreadyEstablishedByAForwardPath()
    {
        // root(0) -> a(1) -> b(2) -> c(3), plus a back edge c(3) -> a(1) forming a cycle.
        // a is already directly reachable from root without going through the cycle, so the back
        // edge must not change idom(a); this also exercises a non-reducible-looking graph, which
        // is the specific case Lengauer-Tarjan (vs. simpler reducible-flow-graph-only algorithms)
        // is required to handle correctly.
        var (succ, pred) = BuildGraph(4, (0, 1), (1, 2), (2, 3), (3, 1));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(4, root: 0, succ, pred);

        idom[1].Should().Be(0, "a is reached directly from root; the back edge from c adds an extra path but doesn't remove the direct one");
        idom[2].Should().Be(1);
        idom[3].Should().Be(2);
    }

    [Fact]
    public void UnreachableNode_GetsNoDominator()
    {
        // root(0) -> a(1). b(2) exists in the node-id space but has no path from root.
        var (succ, pred) = BuildGraph(3, (0, 1));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(3, root: 0, succ, pred);

        idom[1].Should().Be(0);
        idom[2].Should().Be(-1, "b has no path from root and must not be assigned a dominator");
    }

    [Fact]
    public void SingleNodeGraph_RootIsItsOwnDominator()
    {
        var (succ, pred) = BuildGraph(1);

        int[] idom = LengauerTarjan.ComputeImmediateDominators(1, root: 0, succ, pred);

        idom[0].Should().Be(0);
    }

    [Fact]
    public void ThreeWayFanIn_DominatorIsTheCommonAncestorAcrossAllThreeBranches()
    {
        // root(0) -> a(1), root(0) -> b(2), root(0) -> c(3), all three -> d(4)
        var (succ, pred) = BuildGraph(5, (0, 1), (0, 2), (0, 3), (1, 4), (2, 4), (3, 4));

        int[] idom = LengauerTarjan.ComputeImmediateDominators(5, root: 0, succ, pred);

        idom[4].Should().Be(0, "d is reachable via three independent branches, so only root dominates it");
    }
}
