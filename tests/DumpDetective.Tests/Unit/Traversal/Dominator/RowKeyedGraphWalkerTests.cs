using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// Pins <see cref="RowKeyedGraphWalker"/> against <see cref="ReachableGraphWalker"/> on synthetic
/// graphs — the cheap half of R2/R3's correctness gate, before the expensive half (byte-identical
/// dominator columns on a real dump).
/// </summary>
/// <remarks>
/// The two walkers deliberately number nodes differently: the old one in discovery order, the new
/// one in ascending object row (which is ascending address). So these tests compare what must be
/// invariant — the reachable *set*, the edge *multiset*, and each node's degrees — rather than
/// arrays position by position, which would only assert that the ordering had not changed.
/// </remarks>
public class RowKeyedGraphWalkerTests
{
    /// <summary>
    /// Stands in for <c>ScratchFileObjectMetadataLookup</c>: addresses ascend with row, matching the
    /// monotonic object column O1 guarantees.
    /// </summary>
    private sealed class FakeRowResolver(ulong[] ascendingAddresses) : IObjectRowResolver
    {
        public long TryGetRow(ulong address)
        {
            int row = Array.BinarySearch(ascendingAddresses, address);
            return row < 0 ? -1 : row;
        }

        public ulong GetAddress(long globalRow) => ascendingAddresses[globalRow];
    }

    private static SuccessorsFunc EdgesFrom(Dictionary<ulong, ulong[]> graph) =>
        (ulong address, ref ulong[] buffer) =>
        {
            if (!graph.TryGetValue(address, out ulong[]? children))
                return 0;

            if (buffer.Length < children.Length)
                Array.Resize(ref buffer, children.Length);

            children.CopyTo(buffer, 0);
            return children.Length;
        };

    /// <summary>Objects 0x100..0x900, a diamond plus a cycle plus one unreachable island.</summary>
    private static (ulong[] Objects, Dictionary<ulong, ulong[]> Graph, ulong[] Roots) SampleGraph()
    {
        ulong[] objects = [0x100, 0x200, 0x300, 0x400, 0x500, 0x600, 0x700, 0x800, 0x900];
        var graph = new Dictionary<ulong, ulong[]>
        {
            [0x100] = [0x200, 0x300],       // diamond top
            [0x200] = [0x400],
            [0x300] = [0x400],
            [0x400] = [0x500],
            [0x500] = [0x300],              // cycle back into the diamond
            [0x600] = [0x700],              // second root's chain
            [0x700] = [],
            [0x800] = [0x900],              // unreachable island
            [0x900] = [0x800],
        };
        return (objects, graph, [0x100, 0x600]);
    }

    [Fact]
    public void Walk_ReachesExactlyTheSameSetAsTheDiscoveryOrderWalker()
    {
        (ulong[] objects, Dictionary<ulong, ulong[]> graph, ulong[] roots) = SampleGraph();
        SuccessorsFunc successors = EdgesFrom(graph);

        ReachableGraphWalkResult old = ReachableGraphWalker.Walk(
            roots, successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        RowKeyedWalkResult rowKeyed = RowKeyedGraphWalker.Walk(
            roots, successors, new FakeRowResolver(objects), objects.Length,
            buildCsr: true, CancellationToken.None);

        rowKeyed.NodeCount.Should().Be(old.NodeCount);
        rowKeyed.Addresses.Should().BeEquivalentTo(old.ReachableAddresses);
        rowKeyed.Addresses.Should().BeInAscendingOrder("ids are object rows, and rows ascend by address");
        rowKeyed.Addresses.Should().NotContain(0x800UL).And.NotContain(0x900UL);
    }

    [Fact]
    public void Walk_ProducesTheSameEdgeMultisetAsTheDiscoveryOrderWalker()
    {
        (ulong[] objects, Dictionary<ulong, ulong[]> graph, ulong[] roots) = SampleGraph();
        SuccessorsFunc successors = EdgesFrom(graph);

        ReachableGraphWalkResult old = ReachableGraphWalker.Walk(
            roots, successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        RowKeyedWalkResult rowKeyed = RowKeyedGraphWalker.Walk(
            roots, successors, new FakeRowResolver(objects), objects.Length,
            buildCsr: true, CancellationToken.None);

        rowKeyed.EdgeCount.Should().Be(old.EdgeCount);

        // Both CSRs are translated back to addresses so the two id spaces become comparable.
        List<(ulong, ulong)> Forward(ulong[] addresses, int[] offsets, int[] targets, int nodeCount)
        {
            var edges = new List<(ulong, ulong)>();
            for (int id = 0; id < nodeCount; id++)
                for (int e = offsets[id]; e < offsets[id + 1]; e++)
                    edges.Add((addresses[id], addresses[targets[e]]));
            return edges;
        }

        Forward(rowKeyed.Addresses, rowKeyed.FwdOffsets, rowKeyed.FwdTargets, rowKeyed.NodeCount)
            .Should().BeEquivalentTo(Forward(old.Addresses, old.FwdOffsets, old.FwdTargets, old.NodeCount));

        Forward(rowKeyed.Addresses, rowKeyed.RevOffsets, rowKeyed.RevTargets, rowKeyed.NodeCount)
            .Should().BeEquivalentTo(Forward(old.Addresses, old.RevOffsets, old.RevTargets, old.NodeCount));
    }

    [Fact]
    public void Walk_MarksExactlyTheSeededRootsAndKeepsDegreesConsistent()
    {
        (ulong[] objects, Dictionary<ulong, ulong[]> graph, ulong[] roots) = SampleGraph();

        RowKeyedWalkResult result = RowKeyedGraphWalker.Walk(
            roots, EdgesFrom(graph), new FakeRowResolver(objects), objects.Length,
            buildCsr: true, CancellationToken.None);

        var flagged = new List<ulong>();
        for (int id = 0; id < result.NodeCount; id++)
            if (result.IsRoot[id])
                flagged.Add(result.Addresses[id]);

        flagged.Should().BeEquivalentTo(roots);

        for (int id = 0; id < result.NodeCount; id++)
        {
            result.OutDegree[id].Should().Be(result.FwdOffsets[id + 1] - result.FwdOffsets[id]);
            result.InDegree[id].Should().Be(result.RevOffsets[id + 1] - result.RevOffsets[id]);
        }

        result.OutDegree.Sum().Should().Be((int)result.EdgeCount);
        result.InDegree.Sum().Should().Be((int)result.EdgeCount);
    }

    [Fact]
    public void Walk_IgnoresChildAddressesThatAreNotLiveObjects()
    {
        // The case that broke R1's first attempt: conservative root scanning yields tagged or
        // garbage pointers. They must not enter the reachable set, because a bitmap over object
        // rows cannot represent them and every row-aligned column would be misaligned.
        ulong[] objects = [0x100, 0x200];
        var graph = new Dictionary<ulong, ulong[]>
        {
            [0x100] = [0x200, 0xFFFFFF, 0x1000007FFA899B53],
            [0x200] = [],
        };

        RowKeyedWalkResult result = RowKeyedGraphWalker.Walk(
            [0x100, 0xDEADBEEF], EdgesFrom(graph), new FakeRowResolver(objects), objects.Length,
            buildCsr: true, CancellationToken.None);

        result.NodeCount.Should().Be(2);
        result.Addresses.Should().BeEquivalentTo(new ulong[] { 0x100, 0x200 });
        result.EdgeCount.Should().Be(1, "only the edge to a real object survives");
    }

    [Fact]
    public void Walk_MembershipOnly_SkipsTheCsrEntirely()
    {
        (ulong[] objects, Dictionary<ulong, ulong[]> graph, ulong[] roots) = SampleGraph();

        RowKeyedWalkResult result = RowKeyedGraphWalker.Walk(
            roots, EdgesFrom(graph), new FakeRowResolver(objects), objects.Length,
            buildCsr: false, CancellationToken.None);

        result.NodeCount.Should().Be(7);
        result.EdgeCount.Should().Be(0);
        result.FwdTargets.Should().BeEmpty();
        result.VisitedBitmap.Should().NotBeEmpty("membership is the whole point of this mode");
    }

    [Fact]
    public void Walk_BitmapPopulationEqualsNodeCount()
    {
        // The invariant R1's failure violated: the bitmap's population is what ReachableRowBitmap
        // persists, and every row-aligned column is sized from NodeCount. If they disagree, every
        // reader refuses to open and the run silently degrades to live ClrMD walks.
        (ulong[] objects, Dictionary<ulong, ulong[]> graph, ulong[] roots) = SampleGraph();

        RowKeyedWalkResult result = RowKeyedGraphWalker.Walk(
            roots, EdgesFrom(graph), new FakeRowResolver(objects), objects.Length,
            buildCsr: true, CancellationToken.None);

        int population = result.VisitedBitmap.Sum(System.Numerics.BitOperations.PopCount);
        population.Should().Be(result.NodeCount);
    }
}
