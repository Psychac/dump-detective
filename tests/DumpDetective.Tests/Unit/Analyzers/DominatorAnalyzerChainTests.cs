using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analyzers;

/// <summary>
/// P3-3 (docs/analysis/phase1/dominator-analyzer-audit.md): <see cref="DominatorAnalyzer.BuildDominatorChain"/>
/// and its siblings only ever call the SDK <c>heap.dominators</c>/<c>heap.objects</c>/
/// <c>heap.root-paths</c> capability surfaces, so plain dictionary-backed fakes are enough — no real
/// or live-attached heap needed, unlike the pre-retyping version of this test file which required a
/// self-attached live process heap purely to resolve type names.
/// </summary>
public sealed class DominatorAnalyzerChainTests
{
    private sealed class FakeHeapDominatorQuery(
        Dictionary<ulong, ulong> idomByAddress, Dictionary<ulong, ulong> retainedByAddress) : IHeapDominatorQuery
    {
        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) =>
            retainedByAddress.TryGetValue(address, out retainedBytes);

        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) =>
            idomByAddress.TryGetValue(address, out dominatorAddress);

        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) => throw new NotSupportedException();

        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) => throw new NotSupportedException();

        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) => throw new NotSupportedException();
    }

    private sealed class FakeHeapObjectLookup(Dictionary<ulong, string> typeNamesByAddress) : IHeapObjectLookup
    {
        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (!typeNamesByAddress.TryGetValue(address, out string? name))
            {
                heapObject = default;
                return false;
            }

            heapObject = MakeObject(address, name);
            return true;
        }
    }

    /// <summary>
    /// Minimal fake for §8b (<see cref="DominatorAnalyzer.ComputeCrossTypeOverlap"/>) — only
    /// <see cref="EnumerateObjects"/> is exercised by that method.
    /// </summary>
    private sealed class FakeHeapObjectStream(IReadOnlyList<HeapObjectRef> objects) : IHeapObjectStream
    {
        public IEnumerable<HeapObjectRef> EnumerateObjects() => objects;
    }

    private static HeapObjectRef MakeObject(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), 0, typeName);

    [Fact]
    public void BuildDominatorChain_StopsAtVirtualRoot_OrdersRootFirstLeafLast()
    {
        const ulong leaf = 0x1000, middle = 0x1008, top = 0x1010;
        var idom = new Dictionary<ulong, ulong> { [leaf] = middle, [middle] = top, [top] = 0 };
        var retained = new Dictionary<ulong, ulong> { [leaf] = 100, [middle] = 500, [top] = 1_000 };
        var dominators = new FakeHeapDominatorQuery(idom, retained);
        var objectLookup = new FakeHeapObjectLookup(new Dictionary<ulong, string> { [leaf] = "Leaf", [middle] = "Middle", [top] = "Top" });

        IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(objectLookup, dominators, leaf, maxDepth: 64);

        chain.Should().HaveCount(3);
        chain[0].Address.Should().Be(top);
        chain[0].RetainedBytes.Should().Be(1_000);
        chain[1].Address.Should().Be(middle);
        chain[2].Address.Should().Be(leaf);
        chain[2].RetainedBytes.Should().Be(100);
    }

    [Fact]
    public void BuildDominatorChain_UnknownAddress_ReturnsSingleHopChain()
    {
        const ulong leaf = 0x1000;
        var dominators = new FakeHeapDominatorQuery(new(), new());
        var objectLookup = new FakeHeapObjectLookup(new());

        IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(objectLookup, dominators, leaf, maxDepth: 64);

        chain.Should().HaveCount(1);
        chain[0].Address.Should().Be(leaf);
    }

    [Fact]
    public void BuildDominatorChain_ExceedsMaxDepth_AppendsSentinelInsteadOfLoopingForever()
    {
        const ulong leaf = 0x1000;

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

        var dominators = new FakeHeapDominatorQuery(idom, retained);
        var objectLookup = new FakeHeapObjectLookup(new());

        const int maxDepth = 10;
        IReadOnlyList<DominatorChainHop> chain = DominatorAnalyzer.BuildDominatorChain(objectLookup, dominators, leaf, maxDepth);

        chain.Should().HaveCount(maxDepth + 1); // maxDepth real hops + one sentinel
        chain[0].Address.Should().Be(0); // sentinel is root-most after Reverse()
        chain[0].TypeName.Should().Contain("chain continues");
        chain[^1].Address.Should().Be(leaf);
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
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x100] = "TypeA", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().Be("TypeA");
        hasSameTypeAncestor.Should().BeFalse();
    }

    [Fact]
    public void WalkInstanceAncestry_SkipsSameTypeAncestorForContainingType_ButStillReportsIt()
    {
        // 0x300(TypeB) -> 0x200(TypeB, same type — must be skipped for "containing type", but
        // still marks HasSameTypeAncestor) -> 0x100(TypeA) -> root.
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x200, [0x200] = 0x100, [0x100] = 0 };
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeB", [0x100] = "TypeA", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x300, "TypeB", instances, maxDepth: 64);

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
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeA", [0x150] = "TypeB", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().Be("TypeA");
        hasSameTypeAncestor.Should().BeTrue();
    }

    [Fact]
    public void WalkInstanceAncestry_OnlySameTypeAncestorsAllTheWayUp_NoContainingTypeButFlagsSameType()
    {
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x200, [0x200] = 0 };
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x200] = "TypeB", [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x300, "TypeB", instances, maxDepth: 64);

        containingTypeName.Should().BeNull();
        hasSameTypeAncestor.Should().BeTrue();
    }

    [Fact]
    public void WalkInstanceAncestry_NoInstanceAncestor_ReturnsNullAndFalse()
    {
        var idom = new Dictionary<ulong, ulong> { [0x300] = 0x900, [0x900] = 0 };
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x300] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x300, "TypeB", instances, maxDepth: 64);

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
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var instances = new Dictionary<ulong, string> { [0x1000] = "TypeB" };

        var (containingTypeName, hasSameTypeAncestor) = DominatorAnalyzer.WalkInstanceAncestry(
            dominators, 0x1000, "TypeB", instances, maxDepth: 10);

        containingTypeName.Should().BeNull();
        hasSameTypeAncestor.Should().BeFalse();
    }

    [Fact]
    public void ComputeCrossTypeOverlap_FewerThanTwoCandidates_ReturnsNullWithoutScanning()
    {
        var dominators = new FakeHeapDominatorQuery(new(), new());
        var heapObjects = new FakeHeapObjectStream(Array.Empty<HeapObjectRef>());
        var candidateTypeNames = new HashSet<string> { "TypeA" };

        (var pairs, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            heapObjects, dominators, candidateTypeNames, new RetentionOptions(), CancellationToken.None);

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
        var dominators = new FakeHeapDominatorQuery(idom, retained);
        var heapObjects = new FakeHeapObjectStream([MakeObject(0x100, "TypeA"), MakeObject(0x200, "TypeB"), MakeObject(0x300, "TypeB")]);
        var candidateTypeNames = new HashSet<string> { "TypeA", "TypeB" };

        (var pairs, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            heapObjects, dominators, candidateTypeNames, new RetentionOptions(), CancellationToken.None);

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
        var dominators = new FakeHeapDominatorQuery(idom, retained);
        var heapObjects = new FakeHeapObjectStream([MakeObject(0x100, "TypeA"), MakeObject(0x200, "TypeB"), MakeObject(0x300, "TypeB")]);
        var candidateTypeNames = new HashSet<string> { "TypeA", "TypeB" };

        (var pairs, _) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            heapObjects, dominators, candidateTypeNames, new RetentionOptions(), CancellationToken.None);

        pairs.Should().ContainSingle();
        pairs![0].ContainedInstanceCount.Should().Be(2, "both b1 and b2 are contained within TypeA");
        pairs[0].ContainedRetainedBytes.Should().Be(25, "only b1 (topmost) contributes — b2's bytes are already inside b1's total");
    }

    [Fact]
    public void ComputeCrossTypeOverlap_NonCandidateEntriesAreIgnored()
    {
        var idom = new Dictionary<ulong, ulong> { [0x200] = 0x100, [0x100] = 0 };
        var dominators = new FakeHeapDominatorQuery(idom, new());
        var heapObjects = new FakeHeapObjectStream([MakeObject(0x100, "TypeA"), MakeObject(0x200, "TypeB"), MakeObject(0x999, "NotACandidate")]);
        var candidateTypeNames = new HashSet<string> { "TypeA", "TypeB" };

        (var pairs, _) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            heapObjects, dominators, candidateTypeNames, new RetentionOptions(), CancellationToken.None);

        pairs.Should().ContainSingle();
        pairs![0].ContainedInstanceCount.Should().Be(1);
    }

    [Fact]
    public void ComputeCrossTypeOverlap_ScanExceedsCap_ReturnsCappedTrue()
    {
        var dominators = new FakeHeapDominatorQuery(new(), new());
        var heapObjects = new FakeHeapObjectStream([MakeObject(0x100, "TypeA"), MakeObject(0x200, "TypeB"), MakeObject(0x300, "TypeB")]);
        var candidateTypeNames = new HashSet<string> { "TypeA", "TypeB" };
        var options = new RetentionOptions { MaxCrossTypeOverlapInstancesScanned = 2 };

        (_, bool capped) = DominatorAnalyzer.ComputeCrossTypeOverlap(
            heapObjects, dominators, candidateTypeNames, options, CancellationToken.None);

        capped.Should().BeTrue();
    }

    [Fact]
    public void ComputeRootChains_NoCandidates_ReturnsNullWithoutTouchingRootPaths()
    {
        // No IHeapRootPathQuery passed at all — must return before dereferencing it, same
        // "nothing to do" fast path as ComputeCrossTypeOverlap's fewer-than-two-candidates case.
        IReadOnlyDictionary<string, RootChainSummary>? result = DominatorAnalyzer.ComputeRootChains(
            rootPaths: null!, candidates: [], new RetentionOptions(), CancellationToken.None);

        result.Should().BeNull();
    }
}
