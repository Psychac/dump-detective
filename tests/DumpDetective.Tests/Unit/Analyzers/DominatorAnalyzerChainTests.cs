using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analyzers;

/// <summary>
/// P3-3 (docs/analysis/phase1/dominator-analyzer-audit.md): <see cref="DominatorAnalyzer.BuildDominatorChain"/>
/// walks a fake <see cref="IDominatorTreeProvider"/> exactly like
/// <see cref="DumpDetective.Tests.Unit.Traversal.Dominator.DominatorRetainedSetAggregatorTests"/> does — it
/// only ever calls <c>TryGetImmediateDominator</c>/<c>TryGetRetainedBytes</c>, so a plain
/// address-keyed fixture is enough. <c>heap.GetObject</c> still needs a real <see cref="ClrHeap"/>,
/// so these attach to the test process's own live heap (same low-cost pattern as
/// <c>BoundedGraphWalkTests</c>) rather than requiring a real dump.
/// </summary>
public sealed class DominatorAnalyzerChainTests
{
    private sealed class FakeDominatorTreeProvider(
        Dictionary<ulong, ulong> idomByAddress, Dictionary<ulong, ulong> retainedByAddress) : IDominatorTreeProvider
    {
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) =>
            idomByAddress.TryGetValue(address, out dominatorAddress);

        public bool TryGetRetainedBytes(ulong address, out ulong retainedBytes) =>
            retainedByAddress.TryGetValue(address, out retainedBytes);

        public IEnumerable<ulong> EnumerateRetainedSet(ulong address) => throw new NotSupportedException();

        public ulong TotalRetainedBytes => throw new NotSupportedException();

        public bool TryGetRetainedBytesByMethodTable(ulong methodTable, out ulong retainedBytes) =>
            throw new NotSupportedException();
    }

    private static ClrHeap GetLiveHeap(out IDisposable dataTarget)
    {
        DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        dataTarget = target;
        ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
        return runtime.Heap;
    }

    private static ulong FirstValidAddress(ClrHeap heap)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Address != 0)
                return obj.Address;
        }

        throw new InvalidOperationException("No valid heap object found to anchor the fake dominator tree.");
    }

    [Fact]
    public void BuildDominatorChain_StopsAtVirtualRoot_OrdersRootFirstLeafLast()
    {
        ClrHeap heap = GetLiveHeap(out IDisposable dataTarget);
        using (dataTarget)
        {
            ulong leaf = FirstValidAddress(heap);
            ulong middle = leaf + 8; // synthetic address, never dereferenced as a real object
            ulong top = leaf + 16;

            var idom = new Dictionary<ulong, ulong> { [leaf] = middle, [middle] = top, [top] = 0 };
            var retained = new Dictionary<ulong, ulong> { [leaf] = 100, [middle] = 500, [top] = 1_000 };
            var provider = new FakeDominatorTreeProvider(idom, retained);

            IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(heap, provider, leaf, maxDepth: 64);

            chain.Should().HaveCount(3);
            chain[0].Address.Should().Be(top);
            chain[0].RetainedBytes.Should().Be(1_000);
            chain[1].Address.Should().Be(middle);
            chain[2].Address.Should().Be(leaf);
            chain[2].RetainedBytes.Should().Be(100);
        }
    }

    [Fact]
    public void BuildDominatorChain_UnknownAddress_ReturnsSingleHopChain()
    {
        ClrHeap heap = GetLiveHeap(out IDisposable dataTarget);
        using (dataTarget)
        {
            ulong leaf = FirstValidAddress(heap);
            var provider = new FakeDominatorTreeProvider(new(), new());

            IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(heap, provider, leaf, maxDepth: 64);

            chain.Should().HaveCount(1);
            chain[0].Address.Should().Be(leaf);
        }
    }

    [Fact]
    public void BuildDominatorChain_ExceedsMaxDepth_AppendsSentinelInsteadOfLoopingForever()
    {
        ClrHeap heap = GetLiveHeap(out IDisposable dataTarget);
        using (dataTarget)
        {
            ulong leaf = FirstValidAddress(heap);

            // Every address dominates the next one indefinitely — an unbounded chain, the exact
            // pathological shape (e.g. a long linked list) the depth cap exists to guard against.
            var idom = new Dictionary<ulong, ulong>();
            var retained = new Dictionary<ulong, ulong>();
            ulong current = leaf;
            for (int i = 0; i < 200; i++)
            {
                ulong next = leaf + (ulong)((i + 1) * 8);
                idom[current] = next;
                retained[current] = (ulong)i;
                current = next;
            }

            var provider = new FakeDominatorTreeProvider(idom, retained);

            const int maxDepth = 10;
            IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(heap, provider, leaf, maxDepth);

            chain.Should().HaveCount(maxDepth + 1); // maxDepth real hops + one sentinel
            chain[0].Address.Should().Be(0); // sentinel is root-most after Reverse()
            chain[0].TypeName.Should().Contain("chain continues");
            chain[^1].Address.Should().Be(leaf);
        }
    }
}
