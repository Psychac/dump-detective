using System.Linq;

using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// Covers <see cref="IFoldInputs"/> — the holder that lets <see cref="LeafFolder.Fold"/> drop its input
/// arrays mid-flight so the reduced CSR it allocates can reuse that memory rather than stacking on top
/// of it (docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 3.2).
///
/// <para>The risk being guarded is specific and silent: releasing an input one step too early would make
/// Fold read an array the holder has already let go. Nothing would throw — the array's contents are
/// still there, just no longer owned — and the result would simply be a wrong dominator tree, on large
/// dumps only. So the central test asserts <b>output equality against a run whose inputs are never
/// released</b>, with the released arrays actively poisoned in between.</para>
/// </summary>
public class LeafFolderReleaseTests
{
    /// <summary>
    /// An <see cref="IFoldInputs"/> that records release order and <b>poisons</b> each array as it is
    /// released, so a read-after-release corrupts the output instead of passing unnoticed.
    /// </summary>
    private sealed class PoisoningFoldInputs : IFoldInputs
    {
        public int NodeCount { get; init; }
        public int[] OutDegree { get; private set; } = [];
        public int[] InDegree { get; private set; } = [];
        public int[] FwdOffsets { get; private set; } = [];
        public int[] FwdTargets { get; private set; } = [];
        public int[] RevOffsets { get; private set; } = [];
        public int[] RevTargets { get; private set; } = [];
        public ulong[] ShallowSizes { get; init; } = [];
        public bool[] IsRoot { get; init; } = [];

        public List<string> Calls { get; } = [];

        public static PoisoningFoldInputs From(ReachableGraphWalkResult walk, ulong[] sizes) => new()
        {
            NodeCount = walk.NodeCount,
            OutDegree = walk.OutDegree,
            InDegree = walk.InDegree,
            FwdOffsets = walk.FwdOffsets,
            FwdTargets = walk.FwdTargets,
            RevOffsets = walk.RevOffsets,
            RevTargets = walk.RevTargets,
            ShallowSizes = sizes,
            IsRoot = walk.IsRoot,
        };

        public void ReleaseDegreeArrays()
        {
            Calls.Add(nameof(ReleaseDegreeArrays));
            Poison(OutDegree, InDegree);
            OutDegree = [];
            InDegree = [];
        }

        public void ReleaseReverseEdgeArrays()
        {
            Calls.Add(nameof(ReleaseReverseEdgeArrays));
            Poison(RevOffsets, RevTargets);
            RevOffsets = [];
            RevTargets = [];
        }

        public void ReleaseForwardEdgeArrays()
        {
            Calls.Add(nameof(ReleaseForwardEdgeArrays));
            Poison(FwdOffsets, FwdTargets);
            FwdOffsets = [];
            FwdTargets = [];
        }

        // -999 rather than 0: zero is a valid node id and a valid CSR offset, so zeroing could let a
        // read-after-release still produce the right answer by luck.
        private static void Poison(params int[][] arrays)
        {
            foreach (int[] a in arrays)
                Array.Fill(a, -999);
        }
    }

    // root -> a, root -> b, a -> shared, b -> shared, a -> leaf1, b -> leaf2, shared -> leaf3.
    // Deliberately mixes foldable leaves (in-degree 1, out-degree 0) with a shared leaf that must not
    // fold, so the reverse-CSR lookup released partway through is genuinely exercised.
    private static readonly (ulong Parent, ulong Child)[] s_edges =
    [
        (0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL),
        (0x2UL, 0x5UL), (0x3UL, 0x6UL), (0x4UL, 0x7UL),
    ];

    private static (ReachableGraphWalkResult Walk, ulong[] Sizes) BuildWalk()
    {
        SuccessorsFunc successors = SyntheticSuccessors.Build(s_edges);
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            [0x1UL], successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: false, CancellationToken.None);

        var sizes = new ulong[walk.NodeCount];
        for (int i = 0; i < walk.NodeCount; i++)
            sizes[i] = (ulong)((i + 1) * 10);

        return (walk, sizes);
    }

    [Fact]
    public void Fold_WithReleasingHolder_ProducesIdenticalResultToNonReleasingRun()
    {
        // Baseline: separate arrays, never released, so nothing can be poisoned out from under it.
        (ReachableGraphWalkResult baseWalk, ulong[] baseSizes) = BuildWalk();
        LeafFoldResult expected = LeafFolder.Fold(
            baseWalk.NodeCount, baseWalk.OutDegree, baseWalk.InDegree,
            baseWalk.FwdOffsets, baseWalk.FwdTargets, baseWalk.RevOffsets, baseWalk.RevTargets,
            baseSizes, baseWalk.IsRoot);

        (ReachableGraphWalkResult walk, ulong[] sizes) = BuildWalk();
        PoisoningFoldInputs inputs = PoisoningFoldInputs.From(walk, sizes);

        LeafFoldResult actual = LeafFolder.Fold(inputs);

        actual.ReducedNodeCount.Should().Be(expected.ReducedNodeCount);
        actual.OldToNewId.Should().Equal(expected.OldToNewId);
        actual.NewToOldId.Should().Equal(expected.NewToOldId);
        actual.ReducedFwdOffsets.Should().Equal(expected.ReducedFwdOffsets);
        actual.ReducedFwdTargets.Should().Equal(expected.ReducedFwdTargets);
        actual.ReducedInDegree.Should().Equal(expected.ReducedInDegree);
        actual.FoldedBytesByNewId.Should().Equal(expected.FoldedBytesByNewId,
            "folded-leaf bytes are attributed through the reverse CSR, which is released partway through");
    }

    [Fact]
    public void Fold_ReleasesEachInputGroupExactlyOnceInDependencyOrder()
    {
        (ReachableGraphWalkResult walk, ulong[] sizes) = BuildWalk();
        PoisoningFoldInputs inputs = PoisoningFoldInputs.From(walk, sizes);

        LeafFolder.Fold(inputs);

        // Order is load-bearing, not incidental: the forward CSR must outlive the reverse one, because
        // the reduced *reverse* CSR is derived from the reduced *forward* CSR at the end of Fold.
        inputs.Calls.Should().Equal(
            nameof(IFoldInputs.ReleaseDegreeArrays),
            nameof(IFoldInputs.ReleaseReverseEdgeArrays),
            nameof(IFoldInputs.ReleaseForwardEdgeArrays));
    }

    [Fact]
    public void ReachableGraph_IsTheProductionHolder_AndReleasesGroupsIndependently()
    {
        (ReachableGraphWalkResult walk, _) = BuildWalk();
        var graph = new ReachableGraph(
            walk, new ulong[walk.NodeCount], new ulong[walk.NodeCount], new GenerationTag[walk.NodeCount]);

        graph.Should().BeAssignableTo<IFoldInputs>(
            "DominatorTreeComputer passes the graph itself as Fold's holder — that is what makes the "
            + "release reach the only surviving reference");

        graph.ReleaseDegreeArrays();
        graph.OutDegree.Should().BeEmpty();
        graph.InDegree.Should().BeEmpty();
        graph.FwdTargets.Should().NotBeEmpty("releasing degrees must not disturb the forward CSR");
        graph.RevTargets.Should().NotBeEmpty("releasing degrees must not disturb the reverse CSR");

        graph.ReleaseReverseEdgeArrays();
        graph.RevTargets.Should().BeEmpty();
        graph.FwdTargets.Should().NotBeEmpty();

        graph.ReleaseForwardEdgeArrays();
        graph.FwdTargets.Should().BeEmpty();

        // The safety-net ReleaseEdgeAndDegreeArrays() runs unconditionally after Fold, so releasing
        // twice must be harmless.
        graph.ReleaseEdgeAndDegreeArrays();
        graph.ReleaseEdgeAndDegreeArrays();

        graph.EdgeCount.Should().BeGreaterThan(0, "EdgeCount is captured at construction so it survives release");
    }

    [Fact]
    public void ReleaseReducedInDegree_DropsOnlyTheInDegrees()
    {
        (ReachableGraphWalkResult walk, ulong[] sizes) = BuildWalk();
        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, sizes, walk.IsRoot);

        fold.ReducedInDegree.Should().NotBeEmpty();

        fold.ReleaseReducedInDegree();

        fold.ReducedInDegree.Should().BeEmpty();
        // Lengauer-Tarjan reads the forward arrays for its whole run and the caller reads the id maps
        // during the retained-bytes rollup — dropping either would corrupt the tree, not just waste time.
        fold.ReducedFwdTargets.Should().NotBeEmpty();
        fold.ReducedFwdOffsets.Should().NotBeEmpty();
        fold.OldToNewId.Should().NotBeEmpty();
        fold.NewToOldId.Should().NotBeEmpty();
        fold.FoldedBytesByNewId.Should().NotBeEmpty();
    }

    [Fact]
    public void Fold_ReducedInDegree_MatchesTheReducedForwardCsr()
    {
        // The extended reverse CSR DominatorTreeComputer builds is sized entirely from these counts, so
        // if they disagreed with the forward CSR the extended array would be mis-sized and either
        // overflow or leave gaps. Fold no longer materialises a reverse CSR to cross-check against, so
        // this invariant is asserted directly.
        (ReachableGraphWalkResult walk, ulong[] sizes) = BuildWalk();
        LeafFoldResult fold = LeafFolder.Fold(
            walk.NodeCount, walk.OutDegree, walk.InDegree,
            walk.FwdOffsets, walk.FwdTargets, walk.RevOffsets, walk.RevTargets, sizes, walk.IsRoot);

        var recounted = new int[fold.ReducedNodeCount];
        for (int from = 0; from < fold.ReducedNodeCount; from++)
        {
            for (int e = fold.ReducedFwdOffsets[from]; e < fold.ReducedFwdOffsets[from + 1]; e++)
                recounted[fold.ReducedFwdTargets[e]]++;
        }

        fold.ReducedInDegree.Should().Equal(recounted);
        fold.ReducedInDegree.Sum().Should().Be(fold.ReducedFwdTargets.Length,
            "every reduced edge contributes exactly one in-degree");
    }
}
