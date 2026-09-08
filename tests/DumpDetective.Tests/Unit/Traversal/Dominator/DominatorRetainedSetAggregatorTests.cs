using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// <see cref="DominatorRetainedSetAggregator"/>'s ancestor-exclusion logic, tested against a fake
/// <see cref="IDominatorTreeProvider"/> rather than a real dominator tree — the aggregator only ever
/// calls <see cref="IDominatorTreeProvider.TryGetImmediateDominator"/>/<see cref="IDominatorTreeProvider.TryGetRetainedBytes"/>,
/// so a plain address-keyed fixture is enough to exercise every branch.
/// </summary>
public class DominatorRetainedSetAggregatorTests
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

    [Fact]
    public void ComputeExclusiveRetainedBytes_EmptyTargets_ReturnsZero()
    {
        var provider = new FakeDominatorTreeProvider(new(), new());

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, Array.Empty<ulong>());

        result.Should().Be(0);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_SingleReachableTarget_ReturnsItsRetainedBytes()
    {
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0 },
            retainedByAddress: new() { [0x100] = 500 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100 });

        result.Should().Be(500);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_TwoIndependentTargets_SumsBoth()
    {
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0, [0x200] = 0 },
            retainedByAddress: new() { [0x100] = 500, [0x200] = 300 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100, 0x200 });

        result.Should().Be(800);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_TargetDirectlyDominatedByAnotherTarget_ExcludesTheDescendant()
    {
        // 0x200's dominator chain reaches 0x100 (also a target) before the virtual root — 0x200's
        // subtree is already inside 0x100's TryGetRetainedBytes total, so it must not be added again.
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0, [0x200] = 0x100 },
            retainedByAddress: new() { [0x100] = 500, [0x200] = 300 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100, 0x200 });

        result.Should().Be(500);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_TargetIndirectlyDominatedByAnotherTarget_ExcludesTheDescendant()
    {
        // 0x300 -> 0x200 (not a target) -> 0x100 (a target): the exclusion walk must climb past a
        // non-target ancestor to find the shared target ancestor.
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0, [0x200] = 0x100, [0x300] = 0x200 },
            retainedByAddress: new() { [0x100] = 500, [0x300] = 50 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100, 0x300 });

        result.Should().Be(500);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_DuplicateTargetAddress_CountedOnce()
    {
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0 },
            retainedByAddress: new() { [0x100] = 500 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100, 0x100 });

        result.Should().Be(500);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_UnreachableTarget_SkippedRatherThanThrowing()
    {
        var provider = new FakeDominatorTreeProvider(idomByAddress: new(), retainedByAddress: new());

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x999 });

        result.Should().Be(0);
    }

    [Fact]
    public void ComputeExclusiveRetainedBytes_SiblingSubtrees_NeitherExcludesTheOther()
    {
        // 0x100 and 0x200 are both direct children of the virtual root — unrelated dominator-tree
        // nodes, not ancestor/descendant — so both must be summed even though a third, unrelated
        // target (0x300) also exists in the tree.
        var provider = new FakeDominatorTreeProvider(
            idomByAddress: new() { [0x100] = 0, [0x200] = 0, [0x300] = 0x200 },
            retainedByAddress: new() { [0x100] = 500, [0x200] = 300 });

        ulong result = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(provider, new ulong[] { 0x100, 0x200 });

        result.Should().Be(800);
    }
}
