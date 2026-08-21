using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Abstractions;
using DumpDetective.Tests.Unit.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// §10.4/§10.6 (Batch 3, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// <see cref="DominatorTreeReaderProvider"/>, the facade wrapping the three dominator-tree readers —
/// exercises them together the way <c>DominatorAnalyzer</c> now does, rather than each reader in
/// isolation (already covered by <c>DominatorTreeIndexTests</c>/<c>DominatorChildIndexTests</c>/
/// <c>DominatorTreeMetadataTests</c>).
/// </summary>
public class DominatorTreeReaderProviderTests : IDisposable
{
    private readonly string _tempDir;

    public DominatorTreeReaderProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // root(0x1, MT=0xA0) -> a(0x2, MT=0xA1) -> leaf(0x3, MT=0xA1) [folded, out=0, in=1]
    // root(0x1) -> b(0x4, MT=0xA2)
    // Shallow sizes: root=10, a=20, leaf=100, b=5. Retained: leaf=100 (itself), a=120 (a+leaf),
    // b=5, root=135 (root+a+leaf+b).
    private string WriteFullContainer()
    {
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL), (0x2UL, 0x3UL), (0x1UL, 0x4UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            [0x1UL], successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        var methodTables = new ulong[walk.NodeCount];
        var shallowSizes = new ulong[walk.NodeCount];
        var generationTags = new DumpDetective.Core.Enums.GenerationTag[walk.NodeCount];
        var shallowByAddress = new Dictionary<ulong, ulong> { [0x1UL] = 10, [0x2UL] = 20, [0x3UL] = 100, [0x4UL] = 5 };
        var methodTableByAddress = new Dictionary<ulong, ulong> { [0x1UL] = 0xA0, [0x2UL] = 0xA1, [0x3UL] = 0xA1, [0x4UL] = 0xA2 };
        for (int id = 0; id < walk.NodeCount; id++)
        {
            shallowSizes[id] = shallowByAddress[walk.Addresses[id]];
            methodTables[id] = methodTableByAddress[walk.Addresses[id]];
        }

        var graph = new ReachableGraph(walk, methodTables, shallowSizes, generationTags);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, CancellationToken.None);
        LeafFoldResult fold = tree.LeafFold;
        int[] oldIdToRow = DominatorRowMapping.Compute(graph, walk.ReachableAddresses);

        // Same "which surviving parent did each folded-away node fold into" translation
        // BuildAndPersistDominatorTree uses — needed here because b (0x4, out=0, in=1) is foldable
        // too, not just the leaf under a, so a single hardcoded parent address would be wrong.
        var parentNewIdOfFoldedOldId = new int[graph.NodeCount];
        Array.Fill(parentNewIdOfFoldedOldId, -1);
        for (int parentNewId = 0; parentNewId < fold.ReducedNodeCount; parentNewId++)
        {
            for (int e = fold.FoldedLeafOffsets[parentNewId]; e < fold.FoldedLeafOffsets[parentNewId + 1]; e++)
                parentNewIdOfFoldedOldId[fold.FoldedLeafOldIds[e]] = parentNewId;
        }

        var dominatorAddressesByRow = new ulong[graph.NodeCount];
        var retainedBytesByRow = new ulong[graph.NodeCount];
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            int newId = fold.OldToNewId[oldId];
            int row = oldIdToRow[oldId];
            if (newId >= 0)
            {
                int dominatorNewId = tree.Idom[newId];
                dominatorAddressesByRow[row] = dominatorNewId == tree.VirtualRoot ? 0UL : graph.Addresses[fold.NewToOldId[dominatorNewId]];
                retainedBytesByRow[row] = tree.RetainedBytes[newId];
            }
            else
            {
                int parentNewId = parentNewIdOfFoldedOldId[oldId];
                dominatorAddressesByRow[row] = graph.Addresses[fold.NewToOldId[parentNewId]];
                retainedBytesByRow[row] = graph.ShallowSizes[oldId];
            }
        }

        DominatorChildIndexBuildResult childIndex = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);
        DominatorRetainedBytesRollupResult rollup = DominatorRetainedBytesRollup.Compute(graph, tree);

        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using var writer = new CacheContainerWriter(containerPath);
        DominatorReachableAddressWriter.Write(writer, walk.ReachableAddresses);
        DominatorTreeIndexWriter.WriteImmediateDominatorAddresses(writer, dominatorAddressesByRow);
        DominatorTreeIndexWriter.WriteRetainedBytes(writer, retainedBytesByRow);
        DominatorChildIndexWriter.Write(writer, childIndex.ChildOffsetsByRow, childIndex.ChildAddressesByRow);
        DominatorTreeMetadataWriter.Write(writer, rollup);
        writer.Finish();

        return containerPath;
    }

    [Fact]
    public void TryOpen_AllSectionsPresent_ReturnsProvider()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();

        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            provider!.Should().NotBeNull();
        }
    }

    [Fact]
    public void TryGetRetainedBytes_MatchesComputedTreeExactly()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            provider!.TryGetRetainedBytes(0x3UL, out ulong leaf).Should().BeTrue();
            leaf.Should().Be(100UL);

            provider.TryGetRetainedBytes(0x2UL, out ulong a).Should().BeTrue();
            a.Should().Be(120UL, "a's own 20 bytes plus the folded leaf's 100");

            provider.TryGetRetainedBytes(0x4UL, out ulong b).Should().BeTrue();
            b.Should().Be(5UL);

            provider.TryGetRetainedBytes(0x1UL, out ulong root).Should().BeTrue();
            root.Should().Be(135UL, "root retains everything transitively: 10 + 20 + 100 + 5");
        }
    }

    [Fact]
    public void TotalRetainedBytes_MatchesWholeTreeTotal()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            provider!.TotalRetainedBytes.Should().Be(135UL);
        }
    }

    [Fact]
    public void TryGetRetainedBytesByMethodTable_SumsAcrossFoldedAndSurvivingInstances()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            // MT 0xA1 covers both a (surviving, retained 120) and leaf (folded, retained 100) —
            // the per-type rollup must be exact over every reachable instance, not just survivors.
            provider!.TryGetRetainedBytesByMethodTable(0xA1UL, out ulong retained).Should().BeTrue();
            retained.Should().Be(220UL);

            provider.TryGetRetainedBytesByMethodTable(0xA2UL, out ulong bRetained).Should().BeTrue();
            bRetained.Should().Be(5UL);

            provider.TryGetRetainedBytesByMethodTable(0xDEADUL, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void EnumerateRetainedSet_IncludesSelfAndAllDescendantsIncludingFoldedLeaves()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            provider!.EnumerateRetainedSet(0x1UL).Should().BeEquivalentTo([0x1UL, 0x2UL, 0x3UL, 0x4UL], "root retains everything");
            provider.EnumerateRetainedSet(0x2UL).Should().BeEquivalentTo([0x2UL, 0x3UL], "a retains itself plus the folded leaf");
            provider.EnumerateRetainedSet(0x4UL).Should().BeEquivalentTo([0x4UL], "b has no children");
        }
    }

    [Fact]
    public void EnumerateRetainedSet_UnknownAddress_ReturnsEmpty()
    {
        string containerPath = WriteFullContainer();
        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeTrue();
        using (provider)
        {
            provider!.EnumerateRetainedSet(0xDEADUL).Should().BeEmpty();
        }
    }

    [Fact]
    public void TryOpen_MissingChildIndex_ReturnsFalse()
    {
        var successors = SyntheticSuccessors.Build((0x1UL, 0x2UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            [0x1UL], successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        string containerPath = Path.Combine(_tempDir, "partial.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            DominatorReachableAddressWriter.Write(writer, walk.ReachableAddresses);
            DominatorTreeIndexWriter.WriteImmediateDominatorAddresses(writer, new ulong[walk.NodeCount]);
            // Deliberately no child index / metadata sections.
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var containerReader).Should().BeTrue();
        DominatorTreeReaderProvider.TryOpen(containerReader!, out DominatorTreeReaderProvider? provider).Should().BeFalse();
        provider.Should().BeNull();
    }
}
