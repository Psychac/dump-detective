using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// §10.4 (Batch 2b, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// <see cref="DominatorRowMapping"/>, extracted from the retired
/// <c>DominatorChildIndexBuilderTests</c> since <see cref="DominatorRowMapping.Compute"/> itself is
/// untouched by format v7's write-side change (docs/cache/cache-format-clean-slate-redesign.md §4) —
/// only what gets built from its output changed.
/// </summary>
public class DominatorRowMappingTests
{
    [Fact]
    public void Compute_MatchesSortedAddressOrder()
    {
        var successors = SyntheticSuccessors.Build((0x30UL, 0x10UL), (0x30UL, 0x20UL));
        ReachableGraphWalkResult walk = ReachableGraphWalker.Walk(
            [0x30UL], successors, reverseEdgeExtractor: null, buildCsr: true,
            captureSortedAddresses: true, CancellationToken.None);

        var methodTables = new ulong[walk.NodeCount];
        var shallowSizes = new ulong[walk.NodeCount];
        var generationTags = new DumpDetective.Core.Enums.GenerationTag[walk.NodeCount];
        var graph = new ReachableGraph(walk, methodTables, shallowSizes, generationTags);

        int[] oldIdToRow = DominatorRowMapping.Compute(graph, walk.ReachableAddresses);

        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
            walk.ReachableAddresses[oldIdToRow[oldId]].Should().Be(graph.Addresses[oldId]);
    }
}
