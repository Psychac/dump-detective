using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="WeakReferenceAnalyzer"/> had no pre-existing test coverage at all before this
/// retyping. Uses fake, dictionary-backed capability implementations (the same shape
/// <c>TimerLeakAnalyzerRetypingCharacterizationTests</c> established) rather than a live/real heap —
/// this analyzer's logic is purely capability-interface-driven, so every branch is exactly
/// controllable without ClrMD at all.
/// </summary>
public sealed class WeakReferenceAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_AliveWeakHandle_CountsAliveAndTracksTargetType()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup);

        result.TotalWeakHandles.Should().Be(1);
        result.AliveWeakTargets.Should().Be(1);
        result.DeadWeakTargets.Should().Be(0);
        result.TopWeakTargetTypes.Should().ContainSingle(t => t.Name == "MyApp.Cache" && t.Count == 1);
        result.WeakHandleKinds.Should().ContainSingle(k => k.Name == "WeakLong" && k.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_DeadWeakHandle_ZeroAddressCountsAsDead()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakShort, targetAddress: 0);

        WeakReferenceDomainResult result = await Analyze(handles: handles);

        result.TotalWeakHandles.Should().Be(1);
        result.AliveWeakTargets.Should().Be(0);
        result.DeadWeakTargets.Should().Be(1);
        result.DeadTargetRatio.Should().Be(1.0);
    }

    [Fact]
    public async Task AnalyzeAsync_WeakHandle_NonZeroAddressButUnresolvable_CountsAsDead()
    {
        // Address is non-zero but the object lookup has nothing registered for it -- matches the
        // pre-retyping analyzer's own "resolved address required, not just non-zero" check.
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x9999);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: new FakeHeapObjectLookup());

        result.AliveWeakTargets.Should().Be(0);
        result.DeadWeakTargets.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_DependentHandle_DeadKeyZeroAddress_TracksValueType()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddDependent(targetAddress: 0, dependentTargetAddress: 0x2000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "MyApp.CachedValue");

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup);

        result.DependentHandleDeadKeyCount.Should().Be(1);
        result.DependentDeadKeyValueTypes.Should().ContainSingle(t => t.Name == "MyApp.CachedValue" && t.Count == 1);
        result.DependentDeadKeyValueTypesUnresolvedCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_DependentHandle_AliveTargetAddress_NotCountedAsDead()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddDependent(targetAddress: 0x1000, dependentTargetAddress: 0x2000);

        WeakReferenceDomainResult result = await Analyze(handles: handles);

        result.DependentHandleDeadKeyCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_DependentHandle_DeadKeyUnresolvableDependentTarget_CountsUnresolved()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddDependent(targetAddress: 0, dependentTargetAddress: 0x2000);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: new FakeHeapObjectLookup());

        result.DependentDeadKeyValueTypesUnresolvedCount.Should().Be(1);
        result.DependentDeadKeyValueTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_AliveWeakTarget_GenerationDistributionTracksResolvedBucket()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, segments: segments);

        result.AliveWeakTargetGenerationDistribution.Should().ContainSingle(g => g.Name == "Gen2" && g.Count == 1);
        result.AliveWeakTargetGenerationUnresolvedCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_AliveWeakTarget_UnresolvableGeneration_CountsUnresolvedNotABucket()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Poh);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, segments: segments);

        result.AliveWeakTargetGenerationDistribution.Should().BeEmpty();
        result.AliveWeakTargetGenerationUnresolvedCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_AliveWeakTarget_UnreachableFromRoot_CountsHeldOnlyViaWeakReference()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Orphan");

        var reachability = new FakeHeapReachabilityQuery();
        // 0x1000 deliberately not marked reachable -- default false.

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, reachability: reachability);

        result.HeldOnlyViaWeakReferenceCount.Should().Be(1);
        result.HeldOnlyViaWeakReferenceTopTypes.Should().ContainSingle(t => t.Name == "MyApp.Orphan");
        result.HeldOnlyViaWeakReferenceDetectionAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_AliveWeakTarget_ReachableFromRoot_NotCountedAsHeldOnlyViaWeakReference()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Rooted");

        var reachability = new FakeHeapReachabilityQuery();
        reachability.SetReachable(0x1000);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, reachability: reachability);

        result.HeldOnlyViaWeakReferenceCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoReachabilityProvider_DetectionUnavailable()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, reachability: null);

        result.HeldOnlyViaWeakReferenceDetectionAvailable.Should().BeFalse();
        result.HeldOnlyViaWeakReferenceCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_AliveWeakTarget_RetainedBytesFromDominator()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 4096);

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, dominators: dominators);

        result.AliveWeakTargetsRetainedBytes.Should().Be(4096);
        result.AliveWeakTargetsRetainedBytesIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_NoDominatorProvider_RetainedBytesZeroAndNotExact()
    {
        var handles = new FakeHeapHandleQuery();
        handles.AddWeak(HeapHandleKind.WeakLong, targetAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        WeakReferenceDomainResult result = await Analyze(handles: handles, objectLookup: objectLookup, dominators: null);

        result.AliveWeakTargetsRetainedBytes.Should().Be(0);
        result.AliveWeakTargetsRetainedBytesIsExact.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WeakReferenceCandidate_StaleWrapperWhenHandleZero()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNativeInt(0x3000, "m_handle", 0);

        WeakReferenceDomainResult result = await Analyze(fields: fields, candidates: [Candidate(0x3000, "System.WeakReference`1[[MyApp.Foo]]")]);

        result.WeakReferenceObjectCount.Should().Be(1);
        result.StaleWrapperCount.Should().Be(1);
        result.TopStaleWrapperHolderTypes.Should().ContainSingle(t => t.Name == "System.WeakReference`1[[MyApp.Foo]]");
        result.StaleWrapperCountIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_WeakReferenceCandidate_NotStaleWhenHandleNonZero()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNativeInt(0x3000, "m_handle", 123);

        WeakReferenceDomainResult result = await Analyze(fields: fields, candidates: [Candidate(0x3000, "System.WeakReference")]);

        result.WeakReferenceObjectCount.Should().Be(1);
        result.StaleWrapperCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidatesOrHandles_ReturnsEmptyResult()
    {
        WeakReferenceDomainResult result = await Analyze();

        result.TotalWeakHandles.Should().Be(0);
        result.WeakReferenceObjectCount.Should().Be(0);
        result.DeadTargetRatio.Should().Be(0.0);
    }

    [Theory]
    [InlineData("System.WeakReference`1[[System.String, mscorlib]]", true)]
    [InlineData("System.WeakReference", true)]
    [InlineData("System.WeakReferenceExtensions", false)]
    [InlineData("MyApp.Cache", false)]
    public void IsCandidateType_MatchesOnlyWeakReferenceTypeNames(string typeName, bool expected)
    {
        WeakReferenceAnalyzer.IsCandidateType(typeName).Should().Be(expected);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<WeakReferenceDomainResult> Analyze(
        FakeHeapHandleQuery? handles = null,
        FakeHeapSegmentQuery? segments = null,
        FakeHeapObjectLookup? objectLookup = null,
        FakeDumpObjectFieldQuery? fields = null,
        FakeHeapDominatorQuery? dominators = null,
        FakeHeapReachabilityQuery? reachability = null,
        HeapObjectRef[]? candidates = null)
    {
        WeakReferenceAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            HeapHandles = handles ?? new FakeHeapHandleQuery(),
            HeapSegments = segments ?? new FakeHeapSegmentQuery(),
            HeapObjectLookup = objectLookup ?? new FakeHeapObjectLookup(),
            DumpObjectFields = fields ?? new FakeDumpObjectFieldQuery(),
            HeapObjects = new PrecomputedHeapObjectStream(candidates ?? []),
            HeapDominators = dominators,
            HeapReachability = reachability,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (WeakReferenceDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeHeapHandleQuery : IHeapHandleQuery
    {
        private readonly List<HeapHandleRef> _handles = new();

        public void AddWeak(HeapHandleKind kind, ulong targetAddress) =>
            _handles.Add(new HeapHandleRef(kind, targetAddress, TargetTypeDisplayName: ""));

        public void AddDependent(ulong targetAddress, ulong dependentTargetAddress) =>
            _handles.Add(new HeapHandleRef(HeapHandleKind.Dependent, targetAddress, TargetTypeDisplayName: "", DependentTargetAddress: dependentTargetAddress));

        public IEnumerable<HeapHandleRef> EnumerateHandles() => _handles;
    }

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly Dictionary<ulong, HeapGenerationTag> _generations = new();
        public void SetGeneration(ulong address, HeapGenerationTag tag) => _generations[address] = tag;

        public HeapGenerationTag GetGeneration(ulong address) =>
            _generations.TryGetValue(address, out HeapGenerationTag tag) ? tag : HeapGenerationTag.Unknown;

        public IEnumerable<HeapSegmentRef> EnumerateSegments() => [];
        public bool TryGetSegment(ulong address, out HeapSegmentRef segment) { segment = default; return false; }
        public int DumpPointerSize => 8;
        public bool IsServerGc => false;
        public int LogicalHeapCount => 1;
        public bool CanWalkHeap => true;
        public IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false) => [];
        public IEnumerable<HeapFreeBlockRef> EnumerateLohFreeBlocks() => [];
        public IEnumerable<HeapObjectRef> EnumerateCapturedLargeObjects() => [];
        public bool HasLohSatelliteIndex => false;
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, string> _typeNames = new();
        public void SetObject(ulong address, string typeName) => _typeNames[address] = typeName;

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_typeNames.TryGetValue(address, out string? typeName))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);
                return true;
            }
            heapObject = default;
            return false;
        }
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetNativeInt(ulong address, string fieldName, nint value) => _values[(address, fieldName)] = FieldValue.ForNativeInt(value);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer
        }
    }

    private sealed class FakeHeapDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<ulong, ulong> _retained = new();
        public void SetRetainedSize(ulong address, ulong bytes) => _retained[address] = bytes;

        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) => _retained.TryGetValue(address, out retainedBytes);
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }
    }

    private sealed class FakeHeapReachabilityQuery : IHeapReachabilityQuery
    {
        private readonly HashSet<ulong> _reachable = new();
        public void SetReachable(ulong address) => _reachable.Add(address);

        public bool IsReachableFromRoot(ulong address) => _reachable.Contains(address);
    }
}
