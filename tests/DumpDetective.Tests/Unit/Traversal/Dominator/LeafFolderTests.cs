using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public class LeafFolderTests
{
    private static SuccessorsFunc BuildSuccessors(params (ulong Parent, ulong Child)[] edges) =>
        SyntheticSuccessors.Build(edges);

    [Fact]
    public void Fold_SingleParentLeaf_ExcludedAndBytesFoldedIntoParent()
    {
        // root(1) -> a(2) -> leaf(3)   [leaf: out=0, in=1 -> foldable]
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        int rootId = Array.IndexOf(walk.Addresses, 0x1UL);
        int aId = Array.IndexOf(walk.Addresses, 0x2UL);
        int leafId = Array.IndexOf(walk.Addresses, 0x3UL);

        var shallowSizes = new ulong[walk.NodeCount];
        shallowSizes[rootId] = 10;
        shallowSizes[aId] = 20;
        shallowSizes[leafId] = 100;

        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, shallowSizes);

        fold.ReducedNodeCount.Should().Be(2, "leaf is folded away, root and a survive");
        fold.OldToNewId[leafId].Should().Be(-1);
        fold.OldToNewId[rootId].Should().BeGreaterThanOrEqualTo(0);
        fold.OldToNewId[aId].Should().BeGreaterThanOrEqualTo(0);

        int newAId = fold.OldToNewId[aId];
        fold.FoldedBytesByNewId[newAId].Should().Be(100, "leaf's shallow size folds into its sole parent, a");
    }

    [Fact]
    public void Fold_SharedLeafWithMultiplePredecessors_NeverFolded()
    {
        // root(1) -> a(2), root(1) -> b(3), a(2) -> shared(4), b(3) -> shared(4)
        // shared: out=0, in=2 -> NOT foldable (needs real LT to determine its idom).
        var successors = BuildSuccessors(
            (0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        var shallowSizes = new ulong[walk.NodeCount];
        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, shallowSizes);

        int sharedId = Array.IndexOf(walk.Addresses, 0x4UL);
        fold.ReducedNodeCount.Should().Be(4, "no node is foldable here");
        fold.OldToNewId[sharedId].Should().BeGreaterThanOrEqualTo(0, "shared leaf survives — it has 2 predecessors, not 1");
    }

    [Fact]
    public void Fold_ReducedCsr_PreservesEdgesAmongSurvivingNodes()
    {
        // root(1) -> a(2); a(2) -> b(3); a(2) -> leaf(4) [out=0,in=1 -> folded]
        // b(3) -> d(5) [out=0,in=1 -> folded] — b itself has out-degree 1, so b survives (unlike a
        // bare chain tail, which would also be foldable).
        var successors = BuildSuccessors(
            (0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x5UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        var shallowSizes = new ulong[walk.NodeCount];
        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, shallowSizes);

        fold.ReducedNodeCount.Should().Be(3, "root, a, b survive; leaf and d are folded");

        int rootOld = Array.IndexOf(walk.Addresses, 0x1UL);
        int aOld = Array.IndexOf(walk.Addresses, 0x2UL);
        int bOld = Array.IndexOf(walk.Addresses, 0x3UL);
        int rootNew = fold.OldToNewId[rootOld];
        int aNew = fold.OldToNewId[aOld];
        int bNew = fold.OldToNewId[bOld];

        // root -> a edge preserved
        var rootTargets = new List<int>();
        for (int e = fold.ReducedFwdOffsets[rootNew]; e < fold.ReducedFwdOffsets[rootNew + 1]; e++)
            rootTargets.Add(fold.ReducedFwdTargets[e]);
        rootTargets.Should().BeEquivalentTo([aNew]);

        // a -> b edge preserved; a -> leaf edge dropped (leaf no longer exists as a node)
        var aTargets = new List<int>();
        for (int e = fold.ReducedFwdOffsets[aNew]; e < fold.ReducedFwdOffsets[aNew + 1]; e++)
            aTargets.Add(fold.ReducedFwdTargets[e]);
        aTargets.Should().BeEquivalentTo([bNew]);

        // b -> d edge dropped entirely (d folded, and b now has no surviving children)
        fold.ReducedFwdOffsets[bNew].Should().Be(fold.ReducedFwdOffsets[bNew + 1]);
    }

    [Fact]
    public void Fold_NoFoldableNodes_ReducedGraphMatchesOriginalStructure()
    {
        // A pure chain has no foldable node at the tail unless the tail itself has in-degree 1 AND
        // out-degree 0 — root -> a -> b: b qualifies (out=0, in=1), so this graph DOES fold b. Use
        // a fan-out-only graph instead where every leaf has 0 out-degree but shared in-degree > 1
        // is not applicable — test the "all internal, no leaves" case: a 3-cycle has no out-degree-0
        // node at all.
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x1UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk([0x1UL], successors, ExactDominatorTreeBudget.Unlimited, CancellationToken.None);

        var shallowSizes = new ulong[walk.NodeCount];
        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, shallowSizes);

        fold.ReducedNodeCount.Should().Be(walk.NodeCount, "every node in a cycle has out-degree >= 1, so none are foldable");
    }
}
