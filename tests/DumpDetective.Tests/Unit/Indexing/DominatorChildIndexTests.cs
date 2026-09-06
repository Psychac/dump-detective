using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;
using DumpDetective.Tests.Unit.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// The dominator child direction since format v7 — docs/cache/cache-format-clean-slate-redesign.md
/// §4's aggressive option: no persisted child-list section, <see cref="DominatorChildIndexReader"/>
/// derives it by inverting the persisted <c>DominatorImmediateDominatorAddresses</c> row-index column
/// in memory on first use. These scenarios (diamond dominance, a folded leaf surfacing as an ordinary
/// child, several leaves folded under one hub) previously exercised the write-time
/// <c>DominatorChildIndexBuilder</c> directly; ported here as full write-then-read round trips since
/// that pure builder function no longer exists — the equivalent logic now lives inside the reader.
/// </summary>
public class DominatorChildIndexTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorChildIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Builds a synthetic reachable graph, computes its dominator tree, and writes exactly the two
    /// sections <see cref="DominatorChildIndexReader"/> needs — same per-row idom computation
    /// <c>DiskBackedObjectIndexWriter.BuildAndPersistDominatorTree</c> uses (folded leaves resolved to
    /// their one real folding parent), duplicated here rather than shared production API surface for
    /// a two-call test helper.
    /// </summary>
    private (string ContainerPath, ReachableGraph Graph, DominatorTreeComputeResult Tree, int[] OldIdToRow) WriteContainer(
        IReadOnlyList<ulong> rootAddresses, SuccessorsFunc successors)
    {
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            rootAddresses, successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        var methodTables = new ulong[walk.NodeCount];
        var shallowSizes = new ulong[walk.NodeCount];
        var generationTags = new GenerationTag[walk.NodeCount];
        for (int id = 0; id < walk.NodeCount; id++)
            generationTags[id] = GenerationTag.Gen2;

        var graph = new ReachableGraph(walk, methodTables, shallowSizes, generationTags);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);
        LeafFoldResult fold = tree.LeafFold;
        int[] oldIdToRow = DominatorRowMapping.Compute(graph, walk.ReachableAddresses);

        var parentNewIdOfFoldedOldId = new int[graph.NodeCount];
        Array.Fill(parentNewIdOfFoldedOldId, -1);
        for (int parentNewId = 0; parentNewId < fold.ReducedNodeCount; parentNewId++)
        {
            for (int e = fold.FoldedLeafOffsets[parentNewId]; e < fold.FoldedLeafOffsets[parentNewId + 1]; e++)
                parentNewIdOfFoldedOldId[fold.FoldedLeafOldIds[e]] = parentNewId;
        }

        var dominatorRowByRow = new uint[graph.NodeCount];
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            int newId = fold.OldToNewId[oldId];
            uint dominatorRow;
            if (newId >= 0)
            {
                int dominatorNewId = tree.Idom[newId];
                dominatorRow = dominatorNewId == tree.VirtualRoot
                    ? DominatorRowIndex.NoParentRow
                    : (uint)oldIdToRow[fold.NewToOldId[dominatorNewId]];
            }
            else
            {
                int parentNewId = parentNewIdOfFoldedOldId[oldId];
                dominatorRow = (uint)oldIdToRow[fold.NewToOldId[parentNewId]];
            }

            dominatorRowByRow[oldIdToRow[oldId]] = dominatorRow;
        }

        string containerPath = Path.Combine(_tempDir, $"cache-{Guid.NewGuid():N}.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorReachableAddressWriter.Write(writer, walk.ReachableAddresses);
            DominatorTreeIndexWriter.WriteImmediateDominatorRows(writer, dominatorRowByRow);
            writer.Finish();
        }

        return (containerPath, graph, tree, oldIdToRow);
    }

    private static DominatorChildIndexReader OpenReader(string containerPath)
    {
        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeTrue();
        return reader!;
    }

    [Fact]
    public void TryGetChildren_DiamondGraph_RootDominatesAllThreeDescendants()
    {
        // root -> a, root -> b, a -> c, b -> c: c is reachable via both branches, so only root
        // dominates it — root's dominator-tree children are a, b, AND c.
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x2UL, 0x4UL), (0x3UL, 0x4UL));
        (string containerPath, _, _, _) = WriteContainer([0x1UL], successors);

        using DominatorChildIndexReader reader = OpenReader(containerPath);

        reader.TryGetChildren(0x1UL, out ulong[] rootChildren).Should().BeTrue();
        rootChildren.Should().BeEquivalentTo([0x2UL, 0x3UL, 0x4UL]);

        reader.TryGetChildren(0x2UL, out ulong[] aChildren).Should().BeTrue();
        aChildren.Should().BeEmpty("a doesn't dominate anything");

        reader.TryGetChildren(0x4UL, out ulong[] cChildren).Should().BeTrue();
        cChildren.Should().BeEmpty("c has no children of its own");
    }

    [Fact]
    public void TryGetChildren_FoldedSingleLeaf_AppearsAsOrdinaryChild()
    {
        // root -> a -> leaf: leaf is folded away (out=0, in=1, §D8), but §10.5/§5's whole point is
        // that it must still show up as a's child once the child index is derived.
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x2UL, 0x3UL));
        (string containerPath, ReachableGraph graph, DominatorTreeComputeResult tree, _) = WriteContainer([0x1UL], successors);

        tree.LeafFold.OldToNewId[Array.IndexOf(graph.Addresses, 0x3UL)].Should().Be(-1, "leaf must actually be folded for this test to mean anything");

        using DominatorChildIndexReader reader = OpenReader(containerPath);

        reader.TryGetChildren(0x2UL, out ulong[] aChildren).Should().BeTrue();
        aChildren.Should().BeEquivalentTo([0x3UL], "the folded leaf must appear as a's child");

        reader.TryGetChildren(0x1UL, out ulong[] rootChildren).Should().BeTrue();
        rootChildren.Should().BeEquivalentTo([0x2UL]);
    }

    [Fact]
    public void TryGetChildren_MultipleFoldedLeavesUnderSameParent_AllAppearAsChildren()
    {
        // hub -> a, hub -> b, hub -> c: a, b, c all fold into hub (out=0, in=1 each).
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x1UL, 0x3UL), (0x1UL, 0x4UL));
        (string containerPath, _, DominatorTreeComputeResult tree, _) = WriteContainer([0x1UL], successors);

        tree.LeafFold.ReducedNodeCount.Should().Be(1, "only the hub survives folding");

        using DominatorChildIndexReader reader = OpenReader(containerPath);

        reader.TryGetChildren(0x1UL, out ulong[] hubChildren).Should().BeTrue();
        hubChildren.Should().BeEquivalentTo([0x2UL, 0x3UL, 0x4UL]);
    }

    [Fact]
    public void TryGetChildren_NoDominatorTreeEdgesAtAll_EveryRowReportsEmpty()
    {
        // Two independent, childless roots — nothing dominates anything, and every row's persisted
        // idom is NoParentRow, so the inversion produces zero child entries for every row.
        var successors = SyntheticSuccessors.Build();
        (string containerPath, _, _, _) = WriteContainer([0x1UL, 0x2UL], successors);

        using DominatorChildIndexReader reader = OpenReader(containerPath);

        reader.TryGetChildren(0x1UL, out ulong[] children1).Should().BeTrue();
        children1.Should().BeEmpty();

        reader.TryGetChildren(0x2UL, out ulong[] children2).Should().BeTrue();
        children2.Should().BeEmpty();
    }

    [Fact]
    public void TryGetChildren_UnknownAddress_ReturnsFalse()
    {
        var successors = SyntheticSuccessors.Build();
        (string containerPath, _, _, _) = WriteContainer([0x1UL], successors);

        using DominatorChildIndexReader reader = OpenReader(containerPath);

        reader.TryGetChildren(0xDEADUL, out ulong[] children).Should().BeFalse();
        children.Should().BeEmpty();
    }

    [Fact]
    public void TryOpen_MissingIdomSection_ReturnsFalse()
    {
        string containerPath = Path.Combine(_tempDir, "cache-partial.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorReachableAddressWriter.Write(writer, [0x100UL]);
            // Deliberately no DominatorImmediateDominatorAddresses — the inversion has nothing to invert.
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? containerReader).Should().BeTrue();
        DominatorChildIndexReader.TryOpen(containerReader!, out DominatorChildIndexReader? reader).Should().BeFalse();
        reader.Should().BeNull();
    }
}
