using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public class DominatorRetainedBytesRollupTests
{
    private static ReachableGraph BuildGraph(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        Dictionary<ulong, ulong> shallowSizesByAddress,
        Dictionary<ulong, ulong> methodTablesByAddress)
    {
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            rootAddresses, successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: false, CancellationToken.None);

        var shallowSizes = new ulong[walk.NodeCount];
        var methodTables = new ulong[walk.NodeCount];
        var generationTags = new GenerationTag[walk.NodeCount];
        for (int id = 0; id < walk.NodeCount; id++)
        {
            shallowSizes[id] = shallowSizesByAddress[walk.Addresses[id]];
            methodTables[id] = methodTablesByAddress[walk.Addresses[id]];
            generationTags[id] = GenerationTag.Gen2;
        }

        return new ReachableGraph(walk, methodTables, shallowSizes, generationTags);
    }

    [Fact]
    public void Compute_SameTypeChainOfSurvivingNodes_TopmostAloneCreditedNotEveryHop()
    {
        // root(X) -> a(Y) -> b(Y) -> c(Y) -> tail(Y) [leaf, folded]. a/b/c all have out-degree 1 so
        // none of them fold — this is the shape (a chain of same-typed nodes, e.g. a linked list)
        // that inflated MT Y's total from O(bytes) to O(bytes x depth) before the fix, since every
        // hop's own RetainedBytes already includes every hop below it.
        var successors = SyntheticSuccessors.Build(
            (0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x3UL, 0x4UL), (0x4UL, 0x5UL));
        var sizes = new Dictionary<ulong, ulong>
        {
            [0x1UL] = 1, [0x2UL] = 10, [0x3UL] = 10, [0x4UL] = 10, [0x5UL] = 10,
        };
        var methodTables = new Dictionary<ulong, ulong>
        {
            [0x1UL] = 0xA0, [0x2UL] = 0xB0, [0x3UL] = 0xB0, [0x4UL] = 0xB0, [0x5UL] = 0xB0,
        };
        ReachableGraph graph = BuildGraph([0x1UL], successors, sizes, methodTables);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        DominatorRetainedBytesRollupResult result = DominatorRetainedBytesRollup.Compute(graph, tree);

        result.RetainedBytesByMethodTable[0xB0].Should().Be(40, "a's own retained bytes (10+10+10+10) already sum the whole chain below it");
        result.RetainedBytesByMethodTable[0xA0].Should().Be(41, "root retains everything transitively");
    }

    [Fact]
    public void Compute_SiblingsOfSameType_BothCreditedSinceNeitherDominatesTheOther()
    {
        // root(X) -> a(Y), root(X) -> b(Y). a and b are dominator-tree siblings, not nested, so
        // both must be credited to Y's total — the fix must not over-exclude unrelated same-typed
        // objects just because they share a MethodTable.
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x1UL, 0x3UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 1, [0x2UL] = 10, [0x3UL] = 20 };
        var methodTables = new Dictionary<ulong, ulong> { [0x1UL] = 0xA0, [0x2UL] = 0xB0, [0x3UL] = 0xB0 };
        ReachableGraph graph = BuildGraph([0x1UL], successors, sizes, methodTables);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        DominatorRetainedBytesRollupResult result = DominatorRetainedBytesRollup.Compute(graph, tree);

        result.RetainedBytesByMethodTable[0xB0].Should().Be(30, "a and b are siblings, neither dominates the other");
    }

    [Fact]
    public void Compute_FoldedLeafSameTypeAsSurvivingParent_NotDoubleCounted()
    {
        // root(X) -> a(Y) -> leaf(Y) [out=0, in=1 -> folded]. Regression case for the double count
        // that motivated this fix: a folded leaf sharing its surviving parent's MethodTable must not
        // be credited twice (once inside the parent's RetainedBytes, once as its own shallow size).
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x2UL, 0x3UL));
        var sizes = new Dictionary<ulong, ulong> { [0x1UL] = 1, [0x2UL] = 20, [0x3UL] = 100 };
        var methodTables = new Dictionary<ulong, ulong> { [0x1UL] = 0xA0, [0x2UL] = 0xB0, [0x3UL] = 0xB0 };
        ReachableGraph graph = BuildGraph([0x1UL], successors, sizes, methodTables);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);

        tree.LeafFold.OldToNewId[Array.IndexOf(graph.Addresses, 0x3UL)].Should().Be(-1, "leaf should have been folded away");

        DominatorRetainedBytesRollupResult result = DominatorRetainedBytesRollup.Compute(graph, tree);

        result.RetainedBytesByMethodTable[0xB0].Should().Be(120, "a's own 20 plus the folded leaf's 100, counted once");
    }
}
