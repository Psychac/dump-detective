using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Tests.Helpers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ReverseEdgeIndexReaderTests : IAsyncLifetime
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

    private async Task<string> BuildContainer(int bucketCount, (ulong Parent, ulong Child)[] edges)
    {
        string containerPath = Path.Combine(_tempDir, $"cache-{Guid.NewGuid():N}.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            await ReverseEdgeCsrTestWriter.WriteAsync(writer, _tempDir, bucketCount, edges);
            writer.Finish();
        }

        return containerPath;
    }

    [Fact]
    public async Task TryGetParents_ReturnsAllRecordedParentsForChild()
    {
        string containerPath = await BuildContainer(3,
            [(0x1000UL, 0x0100UL), (0x2000UL, 0x0100UL), (0x3000UL, 0x0200UL)]);

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            reader!.TryGetParents(0x0100UL, out var parents, out bool truncated).Should().BeTrue();
            parents.Should().BeEquivalentTo(new[] { 0x1000UL, 0x2000UL });
            truncated.Should().BeFalse();

            reader.TryGetParents(0x0200UL, out var parents2, out bool truncated2).Should().BeTrue();
            parents2.Should().BeEquivalentTo(new[] { 0x3000UL });
            truncated2.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TryGetParents_UnknownChild_ReturnsFalse()
    {
        string containerPath = await BuildContainer(2, [(0x1000UL, 0x0100UL)]);

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            reader!.TryGetParents(0xDEAD_BEEFUL, out var parents, out bool truncated).Should().BeFalse();
            parents.Should().BeEmpty();
            truncated.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TryGetParents_ReachableRowWithZeroParents_ReturnsFalse()
    {
        // 0x1000 is only ever a parent, never a child -- reachable (it's in the address set), but
        // with zero recorded in-degree. Matches the retired hash-directory format's "no recorded
        // parents" contract exactly, rather than the finer "reachable but zero-degree" distinction
        // true CSR happens to make available (see the reader's TryGetParents doc comment).
        string containerPath = await BuildContainer(1, [(0x1000UL, 0x0100UL)]);

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            reader!.TryGetParents(0x1000UL, out var parents, out bool truncated).Should().BeFalse();
            parents.Should().BeEmpty();
            truncated.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TryGetParents_HubChildWithManyParents_ReturnsAllUncappedAndNeverTruncated()
    {
        const ulong hotChild = 0x0100UL;
        const int edgeCount = 10_050;
        var edges = new (ulong Parent, ulong Child)[edgeCount];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = ((ulong)(0x10000 + i), hotChild);

        string containerPath = await BuildContainer(1, edges);

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            reader!.TryGetParents(hotChild, out var parents, out bool truncated).Should().BeTrue();
            parents.Should().HaveCount(edgeCount);
            truncated.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TryGetParents_ManyChildrenAcrossBuckets_AllResolveCorrectly()
    {
        var edges = new List<(ulong Parent, ulong Child)>();
        var expectedByChild = new Dictionary<ulong, List<ulong>>();
        for (ulong child = 0; child < 500; child++)
        {
            for (ulong p = 0; p < 3; p++)
            {
                ulong parent = 0x100000UL + child * 10 + p;
                edges.Add((parent, child));
                if (!expectedByChild.TryGetValue(child, out var list))
                    expectedByChild[child] = list = new List<ulong>();
                list.Add(parent);
            }
        }

        string containerPath = await BuildContainer(5, edges.ToArray());

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            foreach (var (child, expectedParents) in expectedByChild)
            {
                reader!.TryGetParents(child, out var parents, out bool truncated).Should().BeTrue();
                parents.Should().BeEquivalentTo(expectedParents);
                truncated.Should().BeFalse();
            }
        }
    }

    [Fact]
    public async Task EnumerateChildCounts_MatchesTryGetParents_ForEveryChild()
    {
        var edges = new List<(ulong Parent, ulong Child)>();
        var expectedByChild = new Dictionary<ulong, int>();
        for (ulong child = 0; child < 500; child++)
        {
            for (ulong p = 0; p < (child % 5) + 1; p++)
            {
                edges.Add((0x100000UL + child * 10 + p, child));
                expectedByChild[child] = expectedByChild.GetValueOrDefault(child) + 1;
            }
        }

        string containerPath = await BuildContainer(5, edges.ToArray());

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            var seen = new Dictionary<ulong, (int Count, bool Truncated)>();
            reader!.EnumerateChildCounts((child, count, truncated) => seen[child] = (count, truncated));

            seen.Keys.Should().BeEquivalentTo(expectedByChild.Keys);
            foreach (var (child, expectedCount) in expectedByChild)
            {
                reader.TryGetParents(child, out var parents, out bool truncatedFromLookup).Should().BeTrue();
                seen[child].Count.Should().Be(expectedCount);
                seen[child].Count.Should().Be(parents.Count);
                seen[child].Truncated.Should().Be(truncatedFromLookup);
            }
        }
    }

    [Fact]
    public async Task EnumerateChildCounts_SkipsRowsWithZeroParents()
    {
        // Mirrors TryGetParents_ReachableRowWithZeroParents_ReturnsFalse: 0x1000 is reachable
        // (it's a recorded parent) but never a child, so it must not appear in the callback at all.
        string containerPath = await BuildContainer(1, [(0x1000UL, 0x0100UL)]);

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeTrue();

        using (reader)
        {
            var seen = new HashSet<ulong>();
            reader!.EnumerateChildCounts((child, _, _) => seen.Add(child));

            seen.Should().BeEquivalentTo([0x0100UL]);
        }
    }

    [Fact]
    public void TryOpen_NoReverseIndexSections_ReturnsFalse()
    {
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeFalse();
        reader.Should().BeNull();
    }

    [Fact]
    public void TryOpen_ReachableAddressesPresentButNoCsr_ReturnsFalse()
    {
        // The two sections are written together in production, but the reader's own precondition
        // (docs/cache/cache-format-clean-slate-redesign.md §2) is specifically that ReverseEdgeOffsets
        // must agree with DominatorReachableAddresses' row count -- absent entirely must fail, same
        // as any other missing satellite section.
        string containerPath = Path.Combine(_tempDir, "cache.bin");
        using (var writer = new CacheContainerWriter(containerPath))
        {
            ObjectColumnSectionsWriter.WriteReachableRows(writer, [0x100UL, 0x200UL]);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out var container).Should().BeTrue();
        ReverseEdgeIndexReader.TryOpen(container!, out var reader).Should().BeFalse();
        reader.Should().BeNull();
    }
}
