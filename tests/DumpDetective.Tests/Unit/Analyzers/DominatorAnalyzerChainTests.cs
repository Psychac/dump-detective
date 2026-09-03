using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

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

    /// <summary>
    /// Minimal fake for §8b (<see cref="DominatorAnalyzer.ComputeCrossTypeOverlap"/>) — only
    /// <see cref="EnumerateIndexedEntriesAsTuples"/> is exercised by that method; everything else
    /// throws, matching <see cref="FakeDominatorTreeProvider"/>'s convention.
    /// </summary>
    private sealed class FakeHeapAnalysisCache(IReadOnlyList<(ulong Address, ulong MethodTable, ulong Size)> entries) : IHeapAnalysisCache
    {
        public long ObjectScanCount => throw new NotSupportedException();
        public long CacheHits => throw new NotSupportedException();
        public long CacheMisses => throw new NotSupportedException();
        public DumpSizeTier SizeTier => throw new NotSupportedException();

        public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap) => throw new NotSupportedException();
        public HashSet<ulong> GetPinnedRootedAddresses(ClrHeap heap) => throw new NotSupportedException();
        public Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> GetStaticFieldsByRootAddress(ClrHeap heap) => throw new NotSupportedException();
        public bool TryResolveStackFrameOwner(ClrHeap heap, ulong rootAddr, out string ownerType, out string methodName) => throw new NotSupportedException();
        public Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap) => throw new NotSupportedException();
        public ulong? GetSampleInstanceAddress(string typeName) => throw new NotSupportedException();
        public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap) => throw new NotSupportedException();
        public IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> GetOrBuildRootTriples(ClrHeap heap) => throw new NotSupportedException();
        public int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount) => throw new NotSupportedException();
        public bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable) => throw new NotSupportedException();
        public bool TryGetObjectMetadata(ClrHeap heap, ulong address, out ulong methodTable, out ulong size) => throw new NotSupportedException();
        public IBackwardReferenceProvider? TryGetReverseIndexProvider() => throw new NotSupportedException();
        public IForwardReferenceProvider? TryGetForwardIndexProvider() => throw new NotSupportedException();
        public IReachableAddressProvider? TryGetReachableAddressProvider() => throw new NotSupportedException();
        public IDominatorTreeProvider? TryGetDominatorTreeProvider() => throw new NotSupportedException();
        public IThreadRetentionProvider? TryGetThreadRetentionProvider() => throw new NotSupportedException();
        public long[]? TryGetGlobalSizeBuckets() => throw new NotSupportedException();

        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples() => entries;
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

    [Fact]
    public void FindContainingCandidateTypeName_AncestorIsAnotherCandidate_ReturnsItsTypeName()
    {
        // chain: root(TypeA) -> middle(not a candidate) -> leaf(TypeB, the chain's own sample).
        var chain = new List<DominatorChainHop>
        {
            new("TypeA", 0x100, 1_000),
            new("NotACandidate", 0x200, 500),
            new("TypeB", 0x300, 100),
        };
        var candidates = new Dictionary<ulong, string> { [0x100] = "TypeA", [0x300] = "TypeB" };

        string? result = DominatorAnalyzer.FindContainingCandidateTypeName(chain, candidates);

        result.Should().Be("TypeA");
    }

    [Fact]
    public void FindContainingCandidateTypeName_NearestCandidateAncestorWins_NotTheOutermost()
    {
        // root(TypeA) -> middle(TypeC, also a candidate) -> leaf(TypeB) — TypeC is the nearer
        // containing candidate, even though TypeA also (transitively) contains TypeB.
        var chain = new List<DominatorChainHop>
        {
            new("TypeA", 0x100, 1_000),
            new("TypeC", 0x200, 500),
            new("TypeB", 0x300, 100),
        };
        var candidates = new Dictionary<ulong, string> { [0x100] = "TypeA", [0x200] = "TypeC", [0x300] = "TypeB" };

        string? result = DominatorAnalyzer.FindContainingCandidateTypeName(chain, candidates);

        result.Should().Be("TypeC");
    }

    [Fact]
    public void FindContainingCandidateTypeName_NoAncestorIsACandidate_ReturnsNull()
    {
        var chain = new List<DominatorChainHop>
        {
            new("NotACandidate", 0x100, 1_000),
            new("TypeB", 0x300, 100),
        };
        var candidates = new Dictionary<ulong, string> { [0x300] = "TypeB" };

        DominatorAnalyzer.FindContainingCandidateTypeName(chain, candidates).Should().BeNull();
    }

    [Fact]
    public void FindContainingCandidateTypeName_ChainHasOnlyItself_ReturnsNull()
    {
        var chain = new List<DominatorChainHop> { new("TypeB", 0x300, 100) };
        var candidates = new Dictionary<ulong, string> { [0x300] = "TypeB" };

        DominatorAnalyzer.FindContainingCandidateTypeName(chain, candidates).Should().BeNull();
    }

    [Fact]
    public void FindContainingCandidateTypeName_SentinelHopAtRoot_IsSkippedNotMatched()
    {
        // Sentinel hops use Address == 0 (never a real object address) — must never be looked up
        // in the candidate map even if, absurdly, 0 were ever a dictionary key.
        var chain = new List<DominatorChainHop>
        {
            new("… chain continues beyond 10 hops", 0, 0),
            new("TypeB", 0x300, 100),
        };
        var candidates = new Dictionary<ulong, string> { [0] = "ShouldNeverMatch" };

        DominatorAnalyzer.FindContainingCandidateTypeName(chain, candidates).Should().BeNull();
    }

    [Fact]
    public void WalkInstanceAncestry_ImmediateParentIsDifferentType_ReturnsItAndNoSameTypeAncestor()
    {
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x100, [0x100] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x100] = "TypeA", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().Be("TypeA");
        hasSameTypeAncestor.Should().BeFalse();
    }

    [Fact]
    public void WalkInstanceAncestry_SkipsSameTypeAncestorForContainingType_ButStillReportsIt()
    {
        // 0x300(TypeB) -> 0x200(TypeB, same type — must be skipped for "containing type", but
        // still marks HasSameTypeAncestor) -> 0x100(TypeA) -> root.
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x200, [0x200] = 0x100, [0x100] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeB", [0x100] = "TypeA", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().Be("TypeA");
        hasSameTypeAncestor.Should().BeTrue();
    }

    [Fact]
    public void WalkInstanceAncestry_SameTypeAncestorBeyondNearestDifferentType_StillDetected()
    {
        // 0x300(TypeB) -> 0x200(TypeA, nearest different type) -> 0x150(TypeB, same type, further
        // up) -> root. The same-type ancestor sits *beyond* the nearest different-type one — a
        // walk that stopped at the first different-type match would miss it.
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x200, [0x200] = 0x150, [0x150] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeA", [0x150] = "TypeB", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().Be("TypeA");
        hasSameTypeAncestor.Should().BeTrue();
    }

    [Fact]
    public void WalkInstanceAncestry_OnlySameTypeAncestorsAllTheWayUp_NoContainingTypeButFlagsSameType()
    {
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x200, [0x200] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeB", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().BeNull();
        hasSameTypeAncestor.Should().BeTrue();
    }

    [Fact]
    public void WalkInstanceAncestry_NoInstanceAncestor_ReturnsNullAndFalse()
    {
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x900, [0x900] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().BeNull();
        hasSameTypeAncestor.Should().BeFalse();
    }

    [Fact]
    public void WalkInstanceAncestry_ExceedsMaxDepth_StopsRatherThanLoopingForever()
    {
        var idom = new Dictionary<ulong, ulong>();
        ulong current = 0x1000;
        for (int i = 0; i < 200; i++)
        {
            ulong next = current + 8;
            idom[current] = next;
            current = next;
        }
        var provider = new FakeDominatorTreeProvider(idom, new());
        var instances = new Dictionary<ulong, string> { [0x1000] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            provider, 0x1000, "TypeB", instances, maxDepth: 10);

        containingTypeName.Should().BeNull();
        hasSameTypeAncestor.Should().BeFalse();
    }

    [Fact]
    public void ComputeCrossTypeOverlap_FewerThanTwoCandidates_ReturnsNullWithoutScanning()
    {
        var provider = new FakeDominatorTreeProvider(new(), new());
        var cache = new FakeHeapAnalysisCache(Array.Empty<(ulong, ulong, ulong)>());
        var candidates = new Dictionary<ulong, string> { [0xA1] = "TypeA" };

        (var pairs, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            cache, provider, candidates, new RetentionOptions(), CancellationToken.None);

        pairs.Should().BeNull();
        capped.Should().BeFalse();
    }

    [Fact]
    public void ComputeCrossTypeOverlap_MultipleInstancesContainedInOneAncestor_CountsThem()
    {
        // Heap: root(TypeA, 0x100) -> b1(TypeB, 0x200), root -> b2(TypeB, 0x300). b1 and b2 are
        // siblings (neither dominates the other) so both are "topmost" for TypeB — both are
        // contained within the single TypeA instance, and §8c must sum both their retained bytes.
        var idom = new Dictionary<ulong, ulong> { [0x200] = 0x100, [0x300] = 0x100, [0x100] = 0 };
        var retained = new Dictionary<ulong, ulong> { [0x200] = 10, [0x300] = 15 };
        var provider = new FakeDominatorTreeProvider(idom, retained);
        var entries = new (ulong, ulong, ulong)[]
        {
            (0x100, 0xA1, 20), (0x200, 0xB1, 10), (0x300, 0xB1, 10),
        };
        var cache = new FakeHeapAnalysisCache(entries);
        var candidates = new Dictionary<ulong, string> { [0xA1] = "TypeA", [0xB1] = "TypeB" };

        (var pairs, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            cache, provider, candidates, new RetentionOptions(), CancellationToken.None);

        capped.Should().BeFalse();
        pairs.Should().ContainSingle();
        pairs![0].TypeName.Should().Be("TypeB");
        pairs[0].ContainingTypeName.Should().Be("TypeA");
        pairs[0].ContainedInstanceCount.Should().Be(2);
        pairs[0].ContainedRetainedBytes.Should().Be(25, "both b1 and b2 are topmost siblings, so both contribute");
    }

    [Fact]
    public void ComputeCrossTypeOverlap_NestedSameTypeInstances_OnlyTopmostBytesCounted()
    {
        // Heap: root(TypeA, 0x100) -> b1(TypeB, 0x200) -> b2(TypeB, 0x300). b2 is dominated by b1
        // (both TypeB), so b1's own retained bytes already include b2's — only b1 (the topmost
        // TypeB instance) should contribute to the (TypeB, TypeA) byte total, though both still
        // count toward the instance count.
        var idom = new Dictionary<ulong, ulong> { [0x200] = 0x100, [0x300] = 0x200, [0x100] = 0 };
        var retained = new Dictionary<ulong, ulong> { [0x200] = 25, [0x300] = 15 }; // b1's 25 already includes b2's 15
        var provider = new FakeDominatorTreeProvider(idom, retained);
        var entries = new (ulong, ulong, ulong)[]
        {
            (0x100, 0xA1, 10), (0x200, 0xB1, 10), (0x300, 0xB1, 15),
        };
        var cache = new FakeHeapAnalysisCache(entries);
        var candidates = new Dictionary<ulong, string> { [0xA1] = "TypeA", [0xB1] = "TypeB" };

        (var pairs, _) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            cache, provider, candidates, new RetentionOptions(), CancellationToken.None);

        pairs.Should().ContainSingle();
        pairs![0].ContainedInstanceCount.Should().Be(2, "both b1 and b2 are contained within TypeA");
        pairs[0].ContainedRetainedBytes.Should().Be(25, "only b1 (topmost) contributes — b2's bytes are already inside b1's total");
    }

    [Fact]
    public void ComputeCrossTypeOverlap_NonCandidateEntriesAreIgnored()
    {
        var idom = new Dictionary<ulong, ulong> { [0x200] = 0x100, [0x100] = 0 };
        var provider = new FakeDominatorTreeProvider(idom, new());
        var entries = new (ulong, ulong, ulong)[]
        {
            (0x100, 0xA1, 20), (0x200, 0xB1, 10), (0x999, 0xFFFF, 5), // 0xFFFF is not a candidate
        };
        var cache = new FakeHeapAnalysisCache(entries);
        var candidates = new Dictionary<ulong, string> { [0xA1] = "TypeA", [0xB1] = "TypeB" };

        (var pairs, _) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            cache, provider, candidates, new RetentionOptions(), CancellationToken.None);

        pairs.Should().ContainSingle();
        pairs![0].ContainedInstanceCount.Should().Be(1);
    }

    [Fact]
    public void ComputeCrossTypeOverlap_ScanExceedsCap_ReturnsCappedTrue()
    {
        var provider = new FakeDominatorTreeProvider(new Dictionary<ulong, ulong> { [0] = 0 }, new());
        var entries = new (ulong, ulong, ulong)[]
        {
            (0x100, 0xA1, 20), (0x200, 0xB1, 10), (0x300, 0xB1, 10),
        };
        var cache = new FakeHeapAnalysisCache(entries);
        var candidates = new Dictionary<ulong, string> { [0xA1] = "TypeA", [0xB1] = "TypeB" };
        var options = new RetentionOptions { MaxCrossTypeOverlapInstancesScanned = 2 };

        (_, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            cache, provider, candidates, options, CancellationToken.None);

        capped.Should().BeTrue();
    }

    [Fact]
    public void ComputeRootChains_NoCandidates_ReturnsNullWithoutTouchingHeapOrCache()
    {
        // No ClrHeap/IHeapAnalysisCache passed at all — must return before dereferencing either,
        // same "nothing to do" fast path as ComputeCrossTypeOverlap's fewer-than-two-candidates case.
        IReadOnlyDictionary<string, RootChainSummary>? result = DominatorAnalyzer.ComputeRootChains(
            heap: null!, cache: null!, candidates: [], new RetentionOptions(), CancellationToken.None);

        result.Should().BeNull();
    }
}
