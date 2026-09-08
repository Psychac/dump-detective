using DumpDetective.Analysis.Traversal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal;

public class BidirectionalGraphSearchTests
{
    /// <summary>
    /// Builds forward/backward neighbor functions from a single edge list (parent → child, i.e.
    /// "parent points at child" — the same direction <see cref="Core.Abstractions.IReferenceProvider"/>
    /// walks). The backward function is the exact inverse, matching what a perfect (untruncated)
    /// reverse index would answer.
    /// </summary>
    private static (Func<ulong, IEnumerable<ulong>> Forward, Func<ulong, IEnumerable<ulong>> Backward) BuildGraph(
        params (ulong Parent, ulong Child)[] edges)
    {
        var forward = new Dictionary<ulong, List<ulong>>();
        var backward = new Dictionary<ulong, List<ulong>>();

        foreach (var (parent, child) in edges)
        {
            if (!forward.TryGetValue(parent, out var children))
                forward[parent] = children = new List<ulong>();
            children.Add(child);

            if (!backward.TryGetValue(child, out var parents))
                backward[child] = parents = new List<ulong>();
            parents.Add(parent);
        }

        IEnumerable<ulong> Forward(ulong node) => forward.TryGetValue(node, out var c) ? c : [];
        IEnumerable<ulong> Backward(ulong node) => backward.TryGetValue(node, out var p) ? p : [];

        return (Forward, Backward);
    }

    private static IReadOnlyList<(string RootKind, ulong Address)> Roots(params ulong[] addresses) =>
        addresses.Select(a => ("Stack", a)).ToList();

    [Fact]
    public void TryFindPath_StraightLine_FindsFullPath()
    {
        // root(1) -> a(2) -> b(3) -> target(4)
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (2UL, 3UL), (3UL, 4UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 4UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000,
            maxTotalDepth: 20,
            out string? rootKind,
            out List<ulong>? path,
            out int candidateSetSize,
            out bool budgetExhausted);

        found.Should().BeTrue();
        rootKind.Should().Be("Stack");
        path.Should().Equal(1UL, 2UL, 3UL, 4UL);
        candidateSetSize.Should().BeGreaterThan(0);
        budgetExhausted.Should().BeFalse();
    }

    [Fact]
    public void TryFindPath_TargetIsRootItself_ReturnsSingleNodePath()
    {
        var (fwd, bwd) = BuildGraph((1UL, 2UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 1UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out string? rootKind, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().Equal(1UL);
        rootKind.Should().Be("Stack");
    }

    [Fact]
    public void TryFindPath_RootDirectlyReferencesTarget_ReturnsTwoNodePath()
    {
        var (fwd, bwd) = BuildGraph((1UL, 2UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 2UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void TryFindPath_UnreachableTarget_ReturnsFalse()
    {
        // Two disconnected chains: 1->2, and 3->4 (target). No edge connects them.
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (3UL, 4UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 4UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out string? rootKind, out List<ulong>? path, out _, out bool budgetExhausted);

        found.Should().BeFalse();
        path.Should().BeNull();
        rootKind.Should().BeNull();
        budgetExhausted.Should().BeFalse(); // exhausted the whole (small) graph, not the budget
    }

    [Fact]
    public void TryFindPath_MultipleRoots_FindsPathFromWhicheverRootConnects()
    {
        // root A(1) is disconnected; root B(10) -> 11 -> target(12).
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (10UL, 11UL), (11UL, 12UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 12UL,
            roots: Roots(1UL, 10UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out string? rootKind, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().NotBeNull();
        path![0].Should().Be(10UL);
        path[^1].Should().Be(12UL);
        rootKind.Should().Be("Stack");
    }

    [Fact]
    public void TryFindPath_CycleInGraph_DoesNotHangAndStillFindsPath()
    {
        // root(1) -> 2 -> 3 -> 2 (cycle back to 2) ; 3 -> target(4)
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (2UL, 3UL), (3UL, 2UL), (3UL, 4UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 4UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().StartWith(1UL);
        path.Should().EndWith(4UL);
    }

    [Fact]
    public void TryFindPath_NodeBudgetExhausted_ReturnsFalseWithBudgetExhaustedTrue()
    {
        // A long chain that needs more than maxNodes to reach.
        var edges = new List<(ulong, ulong)>();
        for (ulong i = 1; i < 50; i++)
            edges.Add((i, i + 1));

        var (fwd, bwd) = BuildGraph(edges.ToArray());

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 50UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 5, // far too small to reach node 50
            maxTotalDepth: 100,
            out _, out List<ulong>? path, out int candidateSetSize, out bool budgetExhausted);

        found.Should().BeFalse();
        path.Should().BeNull();
        budgetExhausted.Should().BeTrue();
        candidateSetSize.Should().BeLessThanOrEqualTo(6); // bounded near maxNodes, not the whole 50-node chain
    }

    [Fact]
    public void TryFindPath_DepthBudgetExhausted_ReturnsFalseWithBudgetExhaustedTrue()
    {
        var edges = new List<(ulong, ulong)>();
        for (ulong i = 1; i < 20; i++)
            edges.Add((i, i + 1));

        var (fwd, bwd) = BuildGraph(edges.ToArray());

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 20UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000,
            maxTotalDepth: 2, // path needs 19 hops total, far more than the combined depth budget
            out _, out List<ulong>? path, out _, out bool budgetExhausted);

        found.Should().BeFalse();
        path.Should().BeNull();
        budgetExhausted.Should().BeTrue();
    }

    [Fact]
    public void TryFindPath_TargetHasNoOutgoingReferences_StillFoundViaRealBackwardLookup()
    {
        // This is the scenario the legacy forward-from-target heuristic could miss: target(4) has
        // zero outgoing references of its own (a leaf object, e.g. a plain object with no reference
        // fields), so "explore forward from target" finds nothing. A real backward lookup still
        // finds its true parent chain directly.
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (2UL, 3UL), (3UL, 4UL));
        // (target=4 has no entries as a key in `forward`, i.e. genuinely no outgoing edges.)

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 4UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().Equal(1UL, 2UL, 3UL, 4UL);
    }

    [Fact]
    public void TryFindPath_NoRootsGiven_ReturnsFalse()
    {
        var (fwd, bwd) = BuildGraph((1UL, 2UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 2UL,
            roots: Array.Empty<(string, ulong)>(),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeFalse();
        path.Should().BeNull();
    }

    [Fact]
    public void TryFindPath_ZeroRootAddressesAreSkipped()
    {
        var (fwd, bwd) = BuildGraph((1UL, 2UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 2UL,
            roots: Roots(0UL, 1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void TryFindPath_ReturnedPathIsValidAgainstForwardEdges()
    {
        // Diamond graph: root(1) -> 2, root(1) -> 3, both 2 and 3 -> target(4).
        var (fwd, bwd) = BuildGraph((1UL, 2UL), (1UL, 3UL), (2UL, 4UL), (3UL, 4UL));

        bool found = new BidirectionalGraphSearch().TryFindPath(
            target: 4UL,
            roots: Roots(1UL),
            fwd, bwd,
            maxNodes: 1000, maxTotalDepth: 20,
            out _, out List<ulong>? path, out _, out _);

        found.Should().BeTrue();
        path.Should().NotBeNull();
        path![0].Should().Be(1UL);
        path[^1].Should().Be(4UL);

        // Every consecutive pair in the returned path must be a real forward edge.
        var forwardEdgeSet = new HashSet<(ulong, ulong)> { (1UL, 2UL), (1UL, 3UL), (2UL, 4UL), (3UL, 4UL) };
        for (int i = 0; i < path.Count - 1; i++)
            forwardEdgeSet.Should().Contain((path[i], path[i + 1]));
    }
}
