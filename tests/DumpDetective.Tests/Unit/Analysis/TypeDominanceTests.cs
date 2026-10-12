using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class TypeDominanceTests
{
    [Fact]
    public void GetImmediateDominatorTypePairs_ResolvesMethodTablesToCanonicalTypes_AndAddsCountsThatCanonicalizationMerges()
    {
        var provider = new FakeProvider(
            new DominatorTypePair(0x10, 0x20, 5),
            new DominatorTypePair(0x10, 0x21, 4),
            new DominatorTypePair(0x11, 0x20, 2));
        var names = new Dictionary<ulong, string>
        {
            [0x10] = "App.Holder",
            [0x11] = "App.OtherHolder",
            [0x20] = "App.Svc+<Run>d__8",
            [0x21] = "App.Svc+<Run>d__9",
        };
        var query = new HeapDominatorQuery(provider, null, new NoLookup(), methodTable => names.GetValueOrDefault(methodTable));

        IReadOnlyList<DominatedTypePair> pairs = query.GetImmediateDominatorTypePairs();

        pairs.Select(p => (p.Dominator.CanonicalName, p.Dominated.CanonicalName, p.ObjectCount)).Should().Equal(
            ("App.Holder", "App.Svc+<Run>d__", 9L),
            ("App.OtherHolder", "App.Svc+<Run>d__", 2L));
    }

    [Fact]
    public void GetImmediateDominatorTypePairs_UnresolvableMethodTable_IsNamedByItsAddress_AndNoResolverGivesNothing()
    {
        var provider = new FakeProvider(new DominatorTypePair(0xABC, 0x20, 1));

        new HeapDominatorQuery(provider, null, new NoLookup(), _ => null).GetImmediateDominatorTypePairs().Single().Dominator.CanonicalName.Should().Be("MT:0xabc");
        new HeapDominatorQuery(provider, null, new NoLookup()).GetImmediateDominatorTypePairs().Should().BeEmpty();
    }

    [Fact]
    public async Task EmitTypeDominanceObservations_OneFactPerPair_DominatorFirst_WithTheObjectCount()
    {
        var holder = new TypeRef { CanonicalName = "App.Holder", Fidelity = MatchFidelity.Exact };
        var item = new TypeRef { CanonicalName = "App.Item", Fidelity = MatchFidelity.Exact };
        var dominators = new FakeQuery(new DominatedTypePair(holder, item, 12), new DominatedTypePair(item, item, 3));
        var sink = new CollectingObservationSink();
        AnalysisContext context = new() { Observations = sink, ArtifactId = new ArtifactId("test") };

        await DominatorAnalyzer.EmitTypeDominanceObservationsAsync(dominators, context, CancellationToken.None);

        sink.Observations.Should().HaveCount(2).And.OnlyContain(o => o.ObservationType == "type.dominance" && o.Subjects.Count == 2);
        sink.Observations.Select(o => (o.Subjects[0].JoinKey, o.Subjects[1].JoinKey, o.Measures["object.count"].Value))
            .Should().Equal(("App.Holder", "App.Item", 12d), ("App.Item", "App.Item", 3d));
    }

    private sealed class FakeProvider(params DominatorTypePair[] pairs) : IDominatorTreeProvider
    {
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetRetainedBytes(ulong address, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IEnumerable<ulong> EnumerateRetainedSet(ulong address) => [];
        public ulong TotalRetainedBytes => 0;
        public bool TryGetRetainedBytesByMethodTable(ulong methodTable, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IReadOnlyList<DominatorTypePair> GetImmediateDominatorTypePairs() => pairs;
    }

    private sealed class NoLookup : IHeapObjectLookup
    {
        public bool TryGetObject(ulong address, out HeapObjectRef heapObject) { heapObject = default; return false; }
    }

    private sealed class FakeQuery(params DominatedTypePair[] pairs) : IHeapDominatorQuery
    {
        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) => [];
        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IReadOnlyList<DominatedTypePair> GetImmediateDominatorTypePairs() => pairs;
    }
}
