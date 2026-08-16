using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public class DominatorTreeComputerTests
{
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

    private static ReachableGraph BuildGraph(
        IReadOnlyList<ulong> rootAddresses,
        Func<ulong, IEnumerable<ulong>> successors,
        Dictionary<ulong, ulong> shallowSizesByAddress)
    {
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(rootAddresses, successors, nodeCap: 0, CancellationToken.None);
        walk.CapExceeded.Should().BeFalse();

        var methodTables = new ulong[walk.NodeCount];
        var shallowSizes = new ulong[walk.NodeCount];
        var generationTags = new GenerationTag[walk.NodeCount];
        for (int id = 0; id < walk.NodeCount; id++)
        {
            shallowSizes[id] = shallowSizesByAddress.GetValueOrDefault(walk.Addresses[id], 1UL);
            generationTags[id] = GenerationTag.Gen2;
        }

        return new ReachableGraph(walk, methodTables, shallowSizes, generationTags);
    }

    private static ulong RetainedBytesOf(ReachableGraph graph, DominatorTreeComputeResult result, ulong address)
    {
        int oldId = Array.IndexOf(graph.Addresses, address);
        int newId = result.LeafFold.OldToNewId[oldId];
        newId.Should().BeGreaterThanOrEqualTo(0, $"address {address:X} should not have been folded away");
        return result.RetainedBytes[newId];
    }

    [Fact]
    public void Compute_Diamond_MergePointDominatedByRoot_RetainedBytesSumCorrectly()
    {
        // root(1) -> a(2), root(1) -> b(3), a(2) -> c(4), b(3) -> c(4)
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 1, [0x2UL] = 2, [0x3UL] = 3, [0x4UL] = 4 };
        ReachableGraph graph = BuildGraph([0x1UL], successors, sizes);

        DominatorTreeComputeResult result = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        // c is reachable via both a and b, so neither dominates it — only root does.
        RetainedBytesOf(graph, result, 0x4UL).Should().Be(4, "c has no children in the dominator tree");
        RetainedBytesOf(graph, result, 0x2UL).Should().Be(2, "a doesn't dominate c, so a's subtree is just itself");
        RetainedBytesOf(graph, result, 0x3UL).Should().Be(3, "b doesn't dominate c either");
        RetainedBytesOf(graph, result, 0x1UL).Should().Be(1 + 2 + 3 + 4, "root dominates everything");
    }

    [Fact]
    public void Compute_SingleParentLeaf_FoldedBytesIncludedInParentRetainedBytes()
    {
        // root(1) -> a(2) -> leaf(3) [out=0, in=1 -> folded per §D8]
        var successors = BuildSuccessors((0x1UL, 0x2UL), (0x2UL, 0x3UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 1, [0x2UL] = 2, [0x3UL] = 100 };
        ReachableGraph graph = BuildGraph([0x1UL], successors, sizes);

        DominatorTreeComputeResult result = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        int leafOldId = Array.IndexOf(graph.Addresses, 0x3UL);
        result.LeafFold.OldToNewId[leafOldId].Should().Be(-1, "leaf should have been folded away");

        RetainedBytesOf(graph, result, 0x2UL).Should().Be(2 + 100, "a's own size plus the folded leaf's shallow size");
        RetainedBytesOf(graph, result, 0x1UL).Should().Be(1 + 2 + 100, "root retains everything transitively");
    }

    [Fact]
    public void Compute_MultipleRoots_EachGetsItsOwnSubtreeUnderTheVirtualRoot()
    {
        // Two independent roots, no shared descendants — each root's subtree should be independent.
        var successors = BuildSuccessors((0x1UL, 0x10UL), (0x2UL, 0x20UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 1, [0x10UL] = 10, [0x2UL] = 2, [0x20UL] = 20 };
        ReachableGraph graph = BuildGraph([0x1UL, 0x2UL], successors, sizes);

        DominatorTreeComputeResult result = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        RetainedBytesOf(graph, result, 0x1UL).Should().Be(1 + 10);
        RetainedBytesOf(graph, result, 0x2UL).Should().Be(2 + 20);

        int root1OldId = Array.IndexOf(graph.Addresses, 0x1UL);
        int root2OldId = Array.IndexOf(graph.Addresses, 0x2UL);
        result.Idom[result.LeafFold.OldToNewId[root1OldId]].Should().Be(result.VirtualRoot);
        result.Idom[result.LeafFold.OldToNewId[root2OldId]].Should().Be(result.VirtualRoot);
    }

    [Fact]
    public void Compute_RootWithSingleParentDegreeShape_IsNeverFoldedEvenThoughItLooksFoldable()
    {
        // root(1) -> a(2) -> root(1) is impossible (root has no real predecessor by definition in
        // this graph), so construct the case that actually matters: a root that ALSO happens to be
        // pointed at by another in-graph object, giving it out-degree 0 (no children) and exactly
        // one *real* predecessor — the exact shape LeafFolder would otherwise fold.
        // root(1) [no children, isRoot=true], other(2) -> root(1)
        var successors = BuildSuccessors((0x2UL, 0x1UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 50, [0x2UL] = 5 };
        // Both addresses are roots so the walk discovers 0x1 with out-degree 0; separately seed 0x2
        // as a root too so it's part of the reachable graph despite pointing at 0x1 rather than
        // being pointed at.
        ReachableGraph graph = BuildGraph([0x1UL, 0x2UL], successors, sizes);

        int rootOldId = Array.IndexOf(graph.Addresses, 0x1UL);
        graph.OutDegree[rootOldId].Should().Be(0);
        graph.InDegree[rootOldId].Should().Be(1, "0x2 -> 0x1 is a real edge, matching the foldable shape by degree alone");

        DominatorTreeComputeResult result = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        result.LeafFold.OldToNewId[rootOldId].Should().BeGreaterThanOrEqualTo(0, "a GC root must never be folded, regardless of degree");
        RetainedBytesOf(graph, result, 0x1UL).Should().Be(50, "root survives as its own node with its own retained bytes");
    }
}
