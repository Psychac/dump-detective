using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

/// <summary>
/// Phase B of format v8's true CSR reverse-edge index (docs/cache/cache-format-clean-slate-redesign.md
/// §2), replacing the retired <c>ReverseEdgeSorter</c>'s hash-bucket-sort-directory tests. Builds
/// directly against real Phase A scratch files via <see cref="ReverseEdgeExtractor"/>, same as the
/// tests it replaces, since Phase A itself is unchanged.
/// </summary>
public class ReverseEdgeCsrBuilderTests : IAsyncLifetime
{
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        await Task.CompletedTask;
    }

    private async Task<ReverseEdgeCsrResult> BuildAsync(int bucketCount, (ulong Parent, ulong Child)[] edges, ulong[] sortedReachableAddresses)
    {
        var extractor = new ReverseEdgeExtractor(bucketCount, _tempDir);
        foreach ((ulong parent, ulong child) in edges)
            extractor.RecordEdge(parent, child);
        await extractor.DisposeAsync();

        return await ReverseEdgeCsrBuilder.BuildAsync(_tempDir, bucketCount, sortedReachableAddresses, CancellationToken.None);
    }

    private static ulong[] ReachableSetOf(params (ulong Parent, ulong Child)[] edges) =>
        edges.SelectMany(e => new[] { e.Parent, e.Child }).Distinct().OrderBy(a => a).ToArray();

    /// <summary>Row of <paramref name="address"/> in <paramref name="sortedReachableAddresses"/>, matching what the builder resolved it to.</summary>
    private static int RowOf(ulong[] sortedReachableAddresses, ulong address) => Array.IndexOf(sortedReachableAddresses, address);

    [Fact]
    public async Task BuildAsync_OffsetsLengthIsRowCountPlusOne()
    {
        (ulong Parent, ulong Child)[] edges = [(0x1000UL, 0x0100UL), (0x2000UL, 0x0200UL)];
        ulong[] reachable = ReachableSetOf(edges);

        ReverseEdgeCsrResult csr = await BuildAsync(1, edges, reachable);

        csr.Offsets.Should().HaveCount(reachable.Length + 1);
        csr.Offsets[0].Should().Be(0);
        csr.Offsets[^1].Should().Be((int)csr.TotalEdges);
    }

    [Fact]
    public async Task BuildAsync_GroupsParentsByChildRow()
    {
        (ulong Parent, ulong Child)[] edges =
        [
            (0x1000UL, 0x0100UL),
            (0x2000UL, 0x0100UL),
            (0x3000UL, 0x0200UL),
        ];
        ulong[] reachable = ReachableSetOf(edges);

        ReverseEdgeCsrResult csr = await BuildAsync(1, edges, reachable);

        int row100 = RowOf(reachable, 0x0100UL);
        int row200 = RowOf(reachable, 0x0200UL);

        ulong[] parentsOf100 = ChildrenOf(csr, reachable, row100);
        ulong[] parentsOf200 = ChildrenOf(csr, reachable, row200);

        parentsOf100.Should().BeEquivalentTo([0x1000UL, 0x2000UL]);
        parentsOf200.Should().BeEquivalentTo([0x3000UL]);
    }

    [Fact]
    public async Task BuildAsync_RowsWithNoRecordedParents_HaveEqualOffsets()
    {
        // 0x1000 is only ever a parent, never a child -- its row's own in-degree is zero.
        (ulong Parent, ulong Child)[] edges = [(0x1000UL, 0x0100UL)];
        ulong[] reachable = ReachableSetOf(edges);

        ReverseEdgeCsrResult csr = await BuildAsync(1, edges, reachable);

        int rowParent = RowOf(reachable, 0x1000UL);
        csr.Offsets[rowParent + 1].Should().Be(csr.Offsets[rowParent], "0x1000 has no incoming edges recorded");
    }

    [Fact]
    public async Task BuildAsync_ManyChildrenAcrossBuckets_TotalEdgeCountMatches()
    {
        var edges = new List<(ulong Parent, ulong Child)>();
        for (ulong child = 0; child < 500; child++)
        {
            for (ulong p = 0; p < 3; p++)
                edges.Add((0x100000UL + child * 10 + p, child));
        }

        ulong[] reachable = ReachableSetOf(edges.ToArray());
        ReverseEdgeCsrResult csr = await BuildAsync(5, edges.ToArray(), reachable);

        csr.TotalEdges.Should().Be(edges.Count);
        csr.Children.Should().HaveCount(edges.Count);

        foreach (ulong child in Enumerable.Range(0, 500).Select(i => (ulong)i))
        {
            int row = RowOf(reachable, child);
            ChildrenOf(csr, reachable, row).Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task BuildAsync_HubChildWithManyParents_ReturnsAllUncapped()
    {
        const ulong hub = 0x0100UL;
        const int edgeCount = 10_050;
        var edges = new (ulong Parent, ulong Child)[edgeCount];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = ((ulong)(0x10000 + i), hub);

        ulong[] reachable = ReachableSetOf(edges);
        ReverseEdgeCsrResult csr = await BuildAsync(1, edges, reachable);

        int row = RowOf(reachable, hub);
        ChildrenOf(csr, reachable, row).Should().HaveCount(edgeCount);
    }

    [Fact]
    public async Task BuildAsync_AddressNotInReachableSet_ThrowsInsteadOfSilentlyDegrading()
    {
        // §2.2.1's resolver-miss fallback is deliberately not implemented here -- every edge
        // ReachableGraphWalker records is guaranteed to have both endpoints in its own reachable
        // set, so a miss means a real bug, and this builder fails loudly rather than emitting a
        // partially-encoded CSR.
        var extractor = new ReverseEdgeExtractor(1, _tempDir);
        extractor.RecordEdge(parent: 0x1000UL, child: 0x0100UL);
        await extractor.DisposeAsync();

        // Deliberately omits 0x1000 from the "reachable" set passed to the builder.
        ulong[] incompleteReachableSet = [0x0100UL];

        Func<Task> act = () => ReverseEdgeCsrBuilder.BuildAsync(_tempDir, 1, incompleteReachableSet, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ulong[] ChildrenOf(ReverseEdgeCsrResult csr, ulong[] reachable, int row)
    {
        int start = csr.Offsets[row];
        int end = csr.Offsets[row + 1];
        var result = new ulong[end - start];
        for (int i = 0; i < result.Length; i++)
            result[i] = reachable[csr.Children[start + i]];
        return result;
    }
}
